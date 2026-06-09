'use strict';
/* ═══════════════════════════════════════════════════════════════════════════
   Autodesk Automation Platform — Unified Client Application
   ═══════════════════════════════════════════════════════════════════════════ */

// ── App state ─────────────────────────────────────────────────────────────────
const A = {
  page:          'newpage',
  platform:      null,          // 'acc' | 'bim360' | null
  _firstInit:    true,          // cleared after first SSE init event — used for page restore
  sessionValid:  false,
  sessionAge:    null,
  loginPending:  false,
  loginDetected: false,
  loginStart:    null,          // Date.now() when browser opened
  loginTimer:    null,          // setInterval handle for countdown
  MIN_LOGIN_DISPLAY: 3,         // minimum seconds to show login waiting screen

  projects:      [],
  selectedIds:   new Set(),
  accRunning:    false,
  bim360Running: false,
  exportRunning: false,   // computed: accRunning || bim360Running || chainRunning
  exportPaused:  false,
  exportStatus:  'idle',
  runningPlatform: null,
  chainRunning:  false,
  activeUser:    null,      // Autodesk session user email, null if unknown
  appUser:       null,      // Cloudsfer app auth user email

  progress:      { completed: 0, total: 0 },
  results:       { success: 0, failed: 0, no_dm: 0, skipped: 0, emailsQueued: 0 },
  projStatuses:  {},
  logHidden:     true,
  logs:          [],

  accStats:    { projectCount: 0, pendingCount: 0, completedCount: 0, noDmCount: 0 },
  bim360Stats: { projectCount: 0, pendingCount: 0, completedCount: 0, noDmCount: 0 },
  _chainDiscoverPhase: null,    // 'acc' | 'bim360' | null — tracks discover-all progress
};

// ── Navigation ────────────────────────────────────────────────────────────────
function navigate(page) {
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));

  const pg  = $(`page-${page}`);
  const nav = document.querySelector(`.nav-item[data-page="${page}"]`);
  if (pg)  pg.classList.add('active');
  if (nav) nav.classList.add('active');
  A.page = page;
  try { sessionStorage.setItem('ui_page', page); } catch { /* private/incognito */ }

  // Hide sidebar on full-screen pages (welcome landing + wizard)
  document.body.classList.toggle('wizard-fullscreen', page === 'newpage' || page === 'welcome');

  if (page === 'auth')      { refreshAuthUI(); checkExistingSession(); }
  if (page === 'platforms') refreshPlatformStats();
  if (page === 'projects')  loadProjects();
  if (page === 'export')    syncExportPage();
  if (page === 'logs')      loadLogs();
  if (page === 'newpage')   { if (typeof npGoStep === 'function') { npGoStep(NP.step); if (A.sessionValid && NP.step === 1) npShowSuccess(A.activeUser); } }
}

function selectPlatform(platform) {
  A.platform = platform;
  document.body.dataset.platform = platform;
  try { sessionStorage.setItem('ui_platform', platform); } catch { /* private/incognito */ }

  // ── Reset ALL platform-specific state so nothing bleeds between ACC ↔ BIM360
  A.selectedIds.clear();
  A.projects      = [];
  A.projStatuses  = {};
  A.logs          = [];
  A.results       = { success: 0, failed: 0, no_dm: 0, skipped: 0, emailsQueued: 0 };
  A.progress      = { completed: 0, total: 0 };
  A.exportStatus  = 'idle';
  A.exportPaused  = false;

  // Clear export page UI so previous platform's data is gone
  const projList = $('export-project-list');
  if (projList) projList.innerHTML = '';
  clearTerminal();
  showEl('export-complete-card', false);
  showEl('nodm-retry-section', false);
  showEl('pause-banner', false);
  syncChips();
  syncProgress();

  // Update sidebar platform badge
  const badge = $('nav-badge-platform');
  if (badge) {
    badge.textContent = platform === 'acc' ? 'ACC' : 'BIM360';
    badge.className   = `nav-badge plat-badge ${platform === 'acc' ? 'acc-active-badge' : 'bim-active-badge'}`;
    badge.classList.remove('hidden');
  }

  // Show platform-specific nav section
  showEl('sb-plat-nav', true);
  const lbl = $('sb-plat-label');
  if (lbl) lbl.textContent = platform === 'acc' ? 'ACC Workspace' : 'BIM360 Workspace';

  // Update page titles & descriptions
  if ($('projects-title')) $('projects-title').textContent = platform === 'acc' ? 'ACC Projects' : 'BIM360 Projects';
  if ($('projects-desc'))  $('projects-desc').textContent  = platform === 'acc'
    ? 'Select ACC projects for Files Log export.'
    : 'Select BIM360 projects for Document Log export (Plans + Project Files).';
  if ($('logs-title')) $('logs-title').textContent = platform === 'acc' ? 'ACC Run History' : 'BIM360 Logs & History';

  // BIM360 has pause/resume; ACC export page should hide those controls unless running
  updatePauseResumeUI();

  // Error-logs tab: only meaningful for BIM360
  const errTab = document.querySelector('[data-tab="error-logs"]');
  if (errTab) errTab.style.display = platform === 'bim360' ? '' : 'none';

  navigate('projects');
}

// ── Discover & Export All Platforms (chain flow) ─────────────────────────────
async function discoverAllAndExport() {
  if (A.exportRunning)  { showToast('An export is already running.', 'warning'); return; }
  if (A.loginPending)   { showToast('Login is in progress. Please wait.', 'warning'); return; }
  if (!A.sessionValid)  { showToast('Please authenticate with Autodesk first.', 'warning'); navigate('auth'); return; }
  if (A._chainDiscoverPhase) { showToast('Discovery already in progress.', 'warning'); return; }

  // Navigate to export page with a neutral platform context
  A.projStatuses = {}; A.logs = []; A.progress = { completed: 0, total: 0 };
  A.results = { success: 0, failed: 0, no_dm: 0, skipped: 0, emailsQueued: 0 };
  showEl('export-complete-card', false);
  showEl('nodm-retry-section', false);
  const list = $('export-project-list'); if (list) list.innerHTML = '';
  clearTerminal(); syncChips(); syncProgress();
  navigate('export');
  setExportTitle('Preparing…', 'Step 1 of 3 — Discovering ACC projects…');

  const btn = $('btn-discover-export-all');
  if (btn) { btn.disabled = true; btn.innerHTML = '<span class="spinner"></span> Discovering ACC…'; }

  A._chainDiscoverPhase = 'acc';
  try {
    await api('/api/acc/projects/discover', 'POST');
  } catch (e) {
    A._chainDiscoverPhase = null;
    setExportTitle('Discovery Failed', e.message || 'Could not fetch ACC projects.');
    showToast(`ACC discovery failed: ${e.message}`, 'error');
    _resetDiscoverAllBtn();
  }
}

function _resetDiscoverAllBtn() {
  const btn = $('btn-discover-export-all');
  if (!btn) return;
  btn.disabled = false;
  btn.innerHTML = '<svg viewBox="0 0 20 20" fill="currentColor" width="14"><path fill-rule="evenodd" d="M11.3 1.046A1 1 0 0112 2v5h4a1 1 0 01.82 1.573l-7 10A1 1 0 018 18v-5H4a1 1 0 01-.82-1.573l7-10a1 1 0 011.12-.38z"/></svg> Discover &amp; Export All';
}

// ── SSE connection ────────────────────────────────────────────────────────────
let sse = null, sseRetry = null;

function connectSSE() {
  if (sse) { sse.close(); sse = null; }
  setConn('connecting');
  sse = new EventSource('/events');
  sse.onopen  = () => setConn('connected');
  sse.onerror = () => {
    setConn('disconnected');
    sse.close(); sse = null;
    clearTimeout(sseRetry);
    sseRetry = setTimeout(connectSSE, 3000);
  };
  sse.onmessage = e => {
    let type, data;
    try { ({ type, data } = JSON.parse(e.data)); }
    catch { return; }   // ignore malformed JSON only
    try { handleEvent(type, data); }
    catch (err) { console.error('[SSE handler error]', type, err); }
  };
}

function setConn(state) {
  const dot  = $('conn-dot');
  const dotM = $('conn-dot-mobile');
  const text = $('conn-text');
  if (dot)  dot.className    = `conn-dot ${state}`;
  if (dotM) dotM.className   = `conn-dot ${state}`;
  if (text) text.textContent = state === 'connected' ? 'Connected' : state === 'connecting' ? 'Connecting…' : 'Reconnecting…';
}

// ── Mobile sidebar ────────────────────────────────────────────────────────────
function toggleSidebar() {
  const sb      = $('sidebar');
  const overlay = $('sidebar-overlay');
  if (!sb) return;
  const isOpen = sb.classList.contains('sidebar-open');
  isOpen ? closeSidebar() : openSidebar();
}
function openSidebar() {
  $('sidebar')?.classList.add('sidebar-open');
  $('sidebar-overlay')?.classList.add('show');
}
function closeSidebar() {
  $('sidebar')?.classList.remove('sidebar-open');
  $('sidebar-overlay')?.classList.remove('show');
}

// ── SSE event dispatcher ──────────────────────────────────────────────────────
function handleEvent(type, data) {
  const platform = data && data.platform;   // 'acc' | 'bim360' | undefined
  const isCurrentPlatform = !platform || platform === A.platform;

  switch (type) {

    case 'init':
      A.accRunning     = data.accRunning    || false;
      A.bim360Running  = data.bim360Running || false;
      A.chainRunning   = data.chainRunning  || false;
      A.exportRunning  = A.accRunning || A.bim360Running || A.chainRunning;
      A.exportPaused   = data.isPaused;
      A.loginPending   = data.loginPending;
      A.loginDetected  = data.loginDetected;
      A.runningPlatform = data.runningPlatform;

      if (data.activeUser !== A.activeUser) {
        A.activeUser = data.activeUser || null;
        updateUserDisplay();
      }

      if (data.loginPending && !A.loginStart) {
        A.loginStart = Date.now() - (data.loginElapsed * 1000);
        onLoginBrowserOpen(/* fromReconnect */ true);
      }

      if (data.acc) {
        A.accStats = {
          projectCount:   data.acc.projectCount   || 0,
          pendingCount:   data.acc.pendingCount    || 0,
          completedCount: data.acc.completedCount  || 0,
          noDmCount:      data.acc.noDmCount       || 0,
        };
      }
      if (data.bim360) {
        A.bim360Stats = {
          projectCount:   data.bim360.projectCount   || 0,
          pendingCount:   data.bim360.pendingCount    || 0,
          completedCount: data.bim360.completedCount  || 0,
          noDmCount:      data.bim360.noDmCount       || 0,
        };
      }

      // Replay logs for current platform
      if (A.platform) {
        const liveData = data[A.platform];
        if (liveData && liveData.recentLogs && liveData.recentLogs.length) {
          A.logs = liveData.recentLogs;
          replayLogs(liveData.recentLogs);
        }
        A.progress     = liveData?.progress || { completed: 0, total: 0 };
        { const sr = liveData?.results;
          A.results = sr
            ? { success: sr.success || 0, failed: sr.failed || 0,
                no_dm: sr.no_dm ?? sr.noDm ?? 0,
                skipped: sr.skipped || 0, emailsQueued: sr.emailsQueued || 0 }
            : { success: 0, failed: 0, no_dm: 0, skipped: 0, emailsQueued: 0 };
          // Sync NP wizard counters so the "Projects without data management" card updates on reconnect
          const serverNoDm = A.results.no_dm;
          if (serverNoDm > (NP.export.noDm || 0)) NP.export.noDm = serverNoDm;
          NP.export.success      = A.results.success;
          NP.export.completed    = A.progress.completed;
          NP.export.total        = Math.max(NP.export.total || 0, A.progress.total || 0);
          NP.export.accessDenied = Object.values(A.projStatuses || {}).filter(function(s){ return s.status === 'access_denied'; }).length;
          const noDmEl = document.getElementById('np-nodm');
          if (noDmEl) noDmEl.textContent = String(NP.export.noDm);
          const adEl = document.getElementById('np-access-denied');
          if (adEl) adEl.textContent = String(NP.export.accessDenied);
        }
        A.projStatuses = liveData?.projectStatuses || {};
        A.exportStatus = liveData?.exportStatus || 'idle';
      }

      if (data.isRunning) navBadgeExport(true);
      refreshAuthUI();
      refreshPlatformStats();
      syncChips(); syncProgress();
      updatePauseResumeUI();

      // Restore the page the user was on before a browser refresh
      if (A._firstInit) {
        A._firstInit = false;
        try {
          const savedPage     = sessionStorage.getItem('ui_page');
          const savedPlatform = sessionStorage.getItem('ui_platform');
          if (savedPage && savedPage !== 'welcome' && savedPlatform) {
            selectPlatform(savedPlatform);
            navigate(savedPage);
          }
        } catch { /* sessionStorage unavailable */ }
      }
      break;

    case 'log':
      if (!platform || platform === A.platform || platform === A.runningPlatform || platform === 'auth') {
        A.logs.push(data);
        appendLog(data);
        aecAdvanceFromLog(data.message || '');
      }
      break;

    case 'login-status':
      if (data.status === 'browser-open') onLoginBrowserOpen();
      if (data.status === 'waiting')      onLoginWaiting(data.elapsed);
      if (data.status === 'completed') {
        if (data.user) { A.activeUser = data.user; updateUserDisplay(); }
        A.sessionValid = true;
        // When on the new wizard page, advance to Step 2 immediately
        if (A.page === 'newpage') {
          if (typeof npClearTimer === 'function') npClearTimer();
          if (typeof npShowSuccess === 'function') npShowSuccess(data.user);
          setTimeout(function() { if (typeof npGoStep === 'function') npGoStep(2); }, 1200);
        } else if (data.source === 'cookie' || data.source === '2-legged') {
          finalizeLogin();
        } else {
          onLoginDetected(data.elapsed);
        }
      }
      if (data.status === 'failed') onLoginFailed(data.error);
      break;

    case 'export-start':
      if (platform === 'acc')    A.accRunning    = true;
      if (platform === 'bim360') A.bim360Running = true;
      A.exportRunning   = true;
      if (platform) { A.runningPlatform = platform; A.platform = platform; }
      {
        A.exportStatus = 'running'; A.exportPaused = false;
        A.logs      = []; clearTerminal();

        // In a chain run OR a wizard multi-platform run, the second export-start must
        // NOT wipe results/statuses already collected by the first phase.
        var _isMulti = A.chainRunning || (typeof NP !== 'undefined' && NP._multiPlatform);
        if (!_isMulti) {
          A.progress  = { completed: 0, total: data.total };
          A.results   = { success: 0, failed: 0, no_dm: 0, skipped: data.skipped || 0, emailsQueued: 0 };
          A.projStatuses = {};
        }

        // Pre-populate THIS platform's projects with their initial status.
        // Explicitly omit files/size so stale values from a previous run don't show.
        if (data.projects && data.projects.length) {
          data.projects.forEach(function(p) {
            const id = p.id || p.name;
            const prev = A.projStatuses[id] || {};
            A.projStatuses[id] = { ...prev, status: p.status || 'pending', name: p.name };
          });
        }

        // Clear stale files/size/status only for projects belonging to THIS platform
        // so the other platform's completed results are preserved in the wizard.
        if (typeof NP !== 'undefined' && NP.projects) {
          NP.projects.forEach(function(p) {
            if (!platform || p.platform === platform) {
              delete p.files; delete p.size; delete p.status;
            }
          });
        }

        if (A.page !== 'export' && A.page !== 'newpage') navigate('export');
        navBadgeExport(true);
        syncChips(); syncProgress();
        showEl('export-complete-card', false);
        showEl('pause-banner', false);
        aecHide();
        setExportTitle('Export in Progress', 'Processing projects one by one…');
        syncExportPage(); updatePauseResumeUI();
      }
      break;

    case 'project-start':
      {
        const id1 = data.project.id || data.project.name;
        A.projStatuses[id1] = { status: 'exporting', name: data.project.name };
        upsertPSI(id1, 'exporting', data.project.name);
        setText('proj-panel-count', `${data.index}/${data.total}`);
        aecShow(data.project.name, data.index, data.total);
      }
      break;

    case 'project-done':
      {
        const id2 = data.project.id || data.project.name;
        // Merge files/size into existing state entry (files-log-summary may have already set them)
        const existing2 = A.projStatuses[id2] || {};
        A.projStatuses[id2] = {
          ...existing2,
          status: data.status,
          name:   data.project.name,
          error:  data.error,
          // Always set fresh values — never inherit stale data from a previous run
          files:     (data.totalFiles > 0) ? Number(data.totalFiles).toLocaleString() : '—',
          size:      data.totalSizeFormatted || '—',
          sizeBytes: data.totalSizeBytes || 0,
        };
        upsertPSI(id2, data.status, data.project.name, data.error);
        aecDone(data.status);
        npSyncProjectDone(data);
      }
      break;

    case 'progress-update':
      A.progress = { completed: data.completed, total: data.total };
      if (data.results) {
        var _multi2 = A.chainRunning || (typeof NP !== 'undefined' && NP._multiPlatform);
        if (_multi2) {
          // Multi-platform: each progress-update only carries one platform's running totals.
          // Use Math.max so accumulated counts from the other platform are never lost.
          var _pr = A.results;
          A.results = {
            success:      Math.max(_pr.success      || 0, data.results.success      || 0),
            failed:       Math.max(_pr.failed       || 0, data.results.failed       || 0),
            no_dm:        Math.max(_pr.no_dm        || 0, data.results.no_dm || data.results.noDm || 0),
            skipped:      Math.max(_pr.skipped      || 0, data.results.skipped      || 0),
            emailsQueued: Math.max(_pr.emailsQueued || 0, data.results.emailsQueued || 0),
          };
        } else {
          A.results = {
            success:      data.results.success      || 0,
            failed:       data.results.failed       || 0,
            no_dm:        data.results.no_dm  || data.results.noDm  || 0,
            skipped:      data.results.skipped      || 0,
            emailsQueued: data.results.emailsQueued || 0,
          };
        }
      }
      syncChips(); syncProgress();
      npSyncProgress(data);
      break;

    case 'export-paused':
      if (isCurrentPlatform) {
        A.exportPaused = true;
        showEl('pause-banner', true);
        updatePauseResumeUI();
      }
      break;

    case 'export-resumed':
      if (isCurrentPlatform) {
        A.exportPaused = false;
        showEl('pause-banner', false);
        updatePauseResumeUI();
      }
      break;

    case 'export-complete':
      if (platform === 'acc')    A.accRunning    = false;
      if (platform === 'bim360') A.bim360Running = false;
      if (!A.chainRunning) {
        A.exportRunning   = A.accRunning || A.bim360Running;
        if (!A.exportRunning) { A.runningPlatform = null; navBadgeExport(false); }
      }
      // Always update results regardless of current platform — export just finished
      A.exportStatus = 'complete'; A.exportPaused = false;
      if (data.results) {
        A.results = {
          success:      data.results.success      || 0,
          failed:       data.results.failed       || 0,
          no_dm:        data.results.no_dm  || data.results.noDm  || 0,
          skipped:      data.results.skipped      || 0,
          emailsQueued: data.results.emailsQueued || 0,
        };
      }
      // Advance progress bar to 100% — final total comes from progress-update; use what we have
      if (A.progress.total > 0)
        A.progress.completed = A.progress.total;
      syncChips(); syncProgress();
      _syncExportSpinners();
      showExportComplete(A.results);
      updatePauseResumeUI();
      refreshPlatformStats();
      loadProjects();
      aecHide();
      npSyncComplete();
      // If the wizard is already on step 5, refresh the results table now
      if (NP.step === 5 && typeof npRenderResults === 'function') npRenderResults();
      break;

    case 'export-error':
      if (platform === 'acc')    A.accRunning    = false;
      if (platform === 'bim360') A.bim360Running = false;
      A.exportRunning = A.accRunning || A.bim360Running || A.chainRunning;
      if (!A.exportRunning) { A.runningPlatform = null; navBadgeExport(false); }
      A._chainDiscoverPhase = null;
      _resetDiscoverAllBtn();
      if (isCurrentPlatform) {
        showToast(`Export error: ${data.error}`, 'error');
        updatePauseResumeUI();
      }
      break;

    case 'discover-complete':
      setPlatDiscoverBusy(platform, false);
      refreshPlatformStats();

      // Chain-discover flow: ACC done → start BIM360; BIM360 done → start export
      if (A._chainDiscoverPhase === 'acc' && platform === 'acc') {
        A._chainDiscoverPhase = 'bim360';
        setExportTitle('Preparing…', 'Step 2 of 3 — Discovering BIM360 projects…');
        const btn2 = $('btn-discover-export-all');
        if (btn2) btn2.innerHTML = '<span class="spinner"></span> Discovering BIM360…';
        (async () => {
          try {
            await api('/api/bim360/projects/discover', 'POST');
          } catch (e) {
            A._chainDiscoverPhase = null;
            setExportTitle('Discovery Failed', e.message || 'Could not fetch BIM360 projects.');
            showToast(`BIM360 discovery failed: ${e.message}`, 'error');
            _resetDiscoverAllBtn();
          }
        })();
        break;
      }
      if (A._chainDiscoverPhase === 'bim360' && platform === 'bim360') {
        A._chainDiscoverPhase = null;
        setExportTitle('Preparing…', 'Step 3 of 3 — Starting export for all platforms…');
        const btn3 = $('btn-discover-export-all');
        if (btn3) btn3.innerHTML = '<span class="spinner"></span> Starting export…';
        (async () => {
          try {
            await api('/api/export/all', 'POST', { fresh: true });
          } catch (e) {
            setExportTitle('Export Failed', e.message || 'Could not start export.');
            showToast(`Export failed: ${e.message}`, 'error');
            _resetDiscoverAllBtn();
          }
        })();
        break;
      }

      // Normal (non-chain) discover — projects page flow
      if (isCurrentPlatform) {
        A.projects = data.projects;
        renderProjectTable(data.projects);
        updateStats(data.projects);
        setDiscoverBusy(false);
        showToast(`Discovered ${data.count} ${platform ? platform.toUpperCase() + ' ' : ''}projects.`, 'success');
      } else {
        showToast(`Discovered ${data.count} ${platform ? platform.toUpperCase() + ' ' : ''}projects.`, 'success');
      }
      break;

    case 'discover-error':
      setPlatDiscoverBusy(platform, false);
      if (A._chainDiscoverPhase) {
        A._chainDiscoverPhase = null;
        setExportTitle('Discovery Failed', data.error || 'Could not fetch projects.');
        _resetDiscoverAllBtn();
      }
      if (isCurrentPlatform) {
        setDiscoverBusy(false);
        showToast(`Discover failed: ${data.error}`, 'error');
      } else {
        showToast(`Discover ${platform ? platform.toUpperCase() + ' ' : ''}failed: ${data.error}`, 'error');
      }
      break;

    case 'export-all-start':
      A.chainRunning  = true;
      A.exportRunning = true;
      A.exportPaused  = false;
      navBadgeExport(true);
      showEl('sb-plat-nav', true);
      showEl('export-complete-card', false);
      showEl('pause-banner', false);
      if (A.page !== 'export') navigate('export');
      setExportTitle('Export All Platforms', 'Preparing export sequence…');
      break;

    case 'export-all-phase':
      // Switch active platform mid-chain without navigating away from export page
      A.platform = data.phase;
      document.body.dataset.platform = data.phase;
      try { sessionStorage.setItem('ui_platform', data.phase); } catch {}
      A.projStatuses = {};
      A.logs = [];
      A.progress = { completed: 0, total: 0 };
      A.results  = { success: 0, failed: 0, no_dm: 0, skipped: 0, emailsQueued: 0 };
      { const el = $('export-project-list'); if (el) el.innerHTML = ''; }
      showEl('nodm-retry-section', false);
      clearTerminal(); syncChips(); syncProgress();
      { const pb = $('nav-badge-platform');
        if (pb) {
          pb.textContent = data.phase === 'acc' ? 'ACC' : 'BIM360';
          pb.className   = `nav-badge plat-badge ${data.phase === 'acc' ? 'acc-active-badge' : 'bim-active-badge'}`;
          pb.classList.remove('hidden');
        }
        const lbl = $('sb-plat-label');
        if (lbl) lbl.textContent = data.phase === 'acc' ? 'ACC Workspace' : 'BIM360 Workspace';
      }
      setExportTitle(
        `${data.phase === 'acc' ? 'ACC' : 'BIM360'} Export`,
        data.phase === 'acc' ? 'Phase 1 — Exporting ACC Files Logs…' : 'Phase 2 — Exporting BIM360 Document Logs…'
      );
      break;

    case 'export-all-complete':
      A.chainRunning    = false;
      A.exportRunning   = false;
      A.runningPlatform = null;
      navBadgeExport(false);
      updatePauseResumeUI();
      refreshPlatformStats();
      _resetDiscoverAllBtn();
      npSyncComplete();
      if (NP.step === 5 && typeof npRenderResults === 'function') npRenderResults();
      break;

    case 'checkpoint-reset':
      if (platform === 'acc')    A.accRunning    = false;
      if (platform === 'bim360') A.bim360Running = false;
      A.exportRunning   = A.accRunning || A.bim360Running;
      A.exportStatus    = 'idle';
      A.exportPaused    = false;
      A.runningPlatform = A.exportRunning ? A.runningPlatform : null;
      A.progress        = { completed: 0, total: 0 };
      A.results         = { success: 0, failed: 0, no_dm: 0, skipped: 0, emailsQueued: 0 };
      A.projStatuses    = {};
      if (!A.exportRunning) navBadgeExport(false);
      syncChips(); syncProgress();
      showEl('export-complete-card', false);
      showEl('pause-banner', false);
      aecHide();
      updatePauseResumeUI();
      setExportTitle('Ready', 'Checkpoint reset — all projects queued for next run.');
      loadProjects();
      // Also refresh wizard project table if on step 3
      if (A.page === 'newpage' && NP.step === 3) npLoadProjects();
      showToast('Checkpoint reset — ' + (data.total || 0) + ' projects re-queued.', 'info');
      break;

    case 'files-log-summary':
      {
        const pid = data.projectId || data.projectName;
        if (pid) {
          // Persist in state so upsertPSI can always read it, regardless of DOM timing
          if (!A.projStatuses[pid]) A.projStatuses[pid] = {};
          A.projStatuses[pid].files = data.totalFiles  != null ? Number(data.totalFiles).toLocaleString() : '—';
          A.projStatuses[pid].size  = data.totalSizeFormatted || '—';
          // Also update live row if it exists
          updatePsiSummary(pid, data.totalFiles, data.totalSizeFormatted);
          // Update wizard NP project entry too
          npSyncFileSummary(pid, A.projStatuses[pid].files, A.projStatuses[pid].size);
        }
      }
      break;

    case 'account-detected':
      // Server saved an admin URL for this platform after detecting the account from the Hubs API.
      // Store it in local state, update any visible URL inputs, and start discovery automatically.
      if (platform === 'acc') {
        A.accAdminUrl = data.url;
        // Fill the URL input on the projects page if visible and empty
        const accInput = $('admin-url-input');
        if (accInput && !accInput.value && A.platform === 'acc') accInput.value = data.url;
      }
      if (platform === 'bim360') {
        A.bim360AdminUrl = data.url;
        const bimInput = $('admin-url-input');
        if (bimInput && !bimInput.value && A.platform === 'bim360') bimInput.value = data.url;
      }
      // Fill wizard URL input
      const npInput = document.getElementById('np-url-input');
      if (npInput && !npInput.value) npInput.value = data.url;
      // Refresh platform stats so project counts update
      refreshPlatformStats();
      showToast(`${platform.toUpperCase()} account detected — "${data.hubName || data.accountId}". URL saved.`, 'success');
      // Auto-start discovery for this platform if no projects are loaded yet
      if (A.sessionValid && !A.exportRunning) {
        const hasProjects = platform === 'acc'
          ? (A.accStats.projectCount || 0) > 0
          : (A.bim360Stats.projectCount || 0) > 0;
        if (!hasProjects) {
          setTimeout(function() {
            api('/api/' + platform + '/projects/discover', 'POST').catch(function() {});
          }, 800);
        }
      }
      break;

    case 'user-changed': {
      // A different user just logged in — wipe ALL state so nothing from the
      // previous user leaks into this session.
      A.activeUser = data.user || null;
      A.projects   = [];
      A.selectedIds.clear();
      A.projStatuses = {};
      A.logs         = [];
      A.results      = { success: 0, failed: 0, no_dm: 0, skipped: 0, emailsQueued: 0 };
      A.progress     = { completed: 0, total: 0 };
      A.exportStatus = 'idle';
      A.exportPaused = false;
      A.exportRunning = false;
      A.runningPlatform = null;
      A.chainRunning = false;
      A.accStats    = { projectCount: 0, pendingCount: 0, completedCount: 0, noDmCount: 0 };
      A.bim360Stats = { projectCount: 0, pendingCount: 0, completedCount: 0, noDmCount: 0 };

      // Clear rendered DOM state (terminal, project list, export card)
      clearTerminal();
      const psiList = $('export-project-list');
      if (psiList) psiList.innerHTML = '';
      const projTbody = $('projects-tbody');
      if (projTbody) projTbody.innerHTML = '';
      showEl('export-complete-card', false);
      showEl('nodm-retry-section', false);
      showEl('pause-banner', false);
      navBadgeExport(false);
      syncChips();
      syncProgress();

      updateUserDisplay();
      refreshPlatformStats();
      // Send the user back to platforms so they start fresh
      // Exception: if on 'newpage' wizard, advance to Step 2 instead of redirecting
      if (A.page === 'newpage') {
        if (typeof npShowSuccess === 'function') npShowSuccess(A.activeUser);
        setTimeout(function() { if (typeof npGoStep === 'function') npGoStep(2); }, 1000);
      } else if (A.page !== 'welcome' && A.page !== 'auth') {
        navigate('platforms');
      }
      showToast(`Signed in as ${A.activeUser || 'account'} — ready to export.`, 'success');
      break;
    }
  }
}

// ── Auth page ─────────────────────────────────────────────────────────────────
async function refreshAuthUI() {
  try {
    const s = await api('/api/status');
    A.sessionValid  = s.sessionValid;
    A.sessionAge    = s.sessionAge;
    A.loginPending  = s.loginPending;
    A.loginDetected = s.loginDetected;

    if (s.accStats)    A.accStats    = s.acc;
    if (s.bim360Stats) A.bim360Stats = s.bim360;

    if (s.loginPending && !A.loginStart) {
      A.loginStart = Date.now() - ((s.loginElapsed || 0) * 1000);
      onLoginBrowserOpen(true);
      return;
    }

    if (s.sessionValid) {
      if (s.activeUser && s.activeUser !== A.activeUser) { A.activeUser = s.activeUser; updateUserDisplay(); }
      showAuthState('success');
      const hoursLeft = Math.max(0, 23 - (s.sessionAge || 0));
      if (s.activeUser) {
        setText('session-age-text', `Active session — ${s.sessionAge}h old, expires in ~${hoursLeft}h.`);
        if (s.sessionWarning) setBanner('warn', `⚠ ${s.sessionWarning}`);
        else setBanner('ok', `✓ Authenticated as ${s.activeUser}. Session expires in ~${hoursLeft}h.`);
      } else {
        setText('session-age-text', 'Session active — re-authenticate to identify your account.');
        setBanner('warn', '⚠ Session is valid but account is unknown. Click Re-authenticate to sign in.');
      }
    } else {
      showAuthState('idle');
      if (s.sessionReason) setBanner('warn', `⚠ ${s.sessionReason}`);
      else hideBanner();
    }
    updateContinueBtn();
    updateNavBadge('auth', s.sessionValid ? '✓' : '');
    refreshPlatformStats();
  } catch { /* server not ready yet */ }
}

function showAuthState(state) {
  showEl('auth-idle',    state === 'idle');
  showEl('auth-pending', state === 'pending');
  showEl('auth-success', state === 'success');

  if (state === 'success') {
    const el = $('auth-user-email');
    if (el) {
      if (A.activeUser) {
        el.textContent = A.activeUser;
        el.classList.remove('hidden');
      } else {
        el.classList.add('hidden');
      }
    }
  }
}

function updateContinueBtn() {
  const btn = $('btn-to-platforms');
  if (btn) btn.disabled = !A.sessionValid || A.loginPending;
}

function setBanner(type, msg) {
  const b = $('session-banner');
  if (!b) return;
  b.className = `alert alert-${type}`;
  b.textContent = msg;
  b.classList.remove('hidden');
}
function hideBanner() { const b = $('session-banner'); if (b) b.classList.add('hidden'); }

// ── Auto-check for existing Autodesk session on page load ────────────────────
// If valid cookies / stored token exist, authenticate silently without a popup.
async function checkExistingSession() {
  try {
    const res = await api('/api/auth/session/check');
    if (res && res.valid) {
      showAuthState('success');
      showToast(`Signed in automatically (${res.user || 'saved session'})`, 'info');
      return true;
    }
  } catch { /* server not ready */ }
  return false;
}

// ── Login flow ────────────────────────────────────────────────────────────────
async function startLogin() {
  try {
    const res = await api('/api/login/start', 'POST', {});

    if (res && res.status === 'completed') {
      // Saved session / cookie path — login completed immediately on the server.
      // No popup needed; just finalize the UI state.
      if (res.user) A.activeUser = res.user;
      finalizeLogin();
      return;
    }

    if (res && res.authUrl) {
      // OAuth popup flow
      const w = 600, h = 700;
      const left = Math.max(0, (screen.width  - w) / 2);
      const top  = Math.max(0, (screen.height - h) / 2);
      window.open(res.authUrl, 'autodesk_oauth',
        `width=${w},height=${h},left=${left},top=${top},toolbar=no,menubar=no,scrollbars=yes`);
      onLoginBrowserOpen();
      return;
    }

    // Playwright browser login — server broadcasts SSE events while Chrome is open
    // (status: "started" with no authUrl). UI already transitions via SSE.
    onLoginBrowserOpen();

  } catch (e) {
    showToast(`Could not start login: ${e.message}`, 'error');
  }
}

function onLoginBrowserOpen(fromReconnect = false) {
  A.loginPending = true;
  if (!A.loginStart) A.loginStart = Date.now();
  showAuthState('pending');
  updateContinueBtn();
  // Start the client-side elapsed timer
  if (A.loginTimer) clearInterval(A.loginTimer);
  A.loginTimer = setInterval(updateLoginTimer, 1000);
  updateLoginTimer();
  setText('wait-status-text', 'Waiting for Autodesk authentication…');
  const chip = $('wait-status-chip');
  if (chip) chip.innerHTML = `<span class="spinner-sm"></span> <span id="wait-status-text">Waiting for Autodesk authentication…</span>`;
}

function onLoginWaiting(serverElapsed) {
  updateLoginTimer();
}

function onLoginDetected(serverElapsed) {
  A.loginDetected = true;
  const clientElapsed = A.loginStart ? Math.floor((Date.now() - A.loginStart) / 1000) : 0;
  // Show "completing" message briefly, then unlock the platform button
  const remaining = Math.max(0, A.MIN_LOGIN_DISPLAY - clientElapsed);
  if (remaining > 0) {
    const chip = $('wait-status-chip');
    if (chip) chip.innerHTML = `<svg viewBox="0 0 20 20" fill="currentColor" width="14" style="color:#059669"><path fill-rule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"/></svg> <span>Login detected! Completing in ${remaining}s…</span>`;
    setTimeout(() => finalizeLogin(), remaining * 1000);
  } else {
    finalizeLogin();
  }
}

function finalizeLogin() {
  if (A.loginTimer) clearInterval(A.loginTimer);
  A.loginPending = false;
  A.sessionValid = true;
  showAuthState('success');
  setText('session-age-text', 'Session saved successfully.');
  setBanner('ok', '✓ Authentication complete. You may now choose a platform.');
  updateContinueBtn();
  updateNavBadge('auth', '✓');
  showToast(`Signed in${A.activeUser ? ' as ' + A.activeUser : ''} successfully!`, 'success');
  refreshAuthUI();
  // Load saved admin URLs and show them on the auth page
  _loadAndShowSavedUrls();
}

async function _loadAndShowSavedUrls() {
  try {
    const s = await api('/api/status');
    const accUrl = s.acc && s.acc.accountAdminUrl;
    const bimUrl = s.bim360 && s.bim360.accountAdminUrl;

    if (accUrl)  A.accAdminUrl    = accUrl;
    if (bimUrl)  A.bim360AdminUrl = bimUrl;

    // Show URLs on the auth success page so the user can confirm them
    const urlDisplay = $('detected-urls-card');
    if (urlDisplay) {
      urlDisplay.innerHTML = '';
      if (accUrl) {
        urlDisplay.innerHTML += `<div class="detected-url-row">
          <span class="detected-url-badge acc-badge">ACC</span>
          <span class="detected-url-text" title="${esc(accUrl)}">${esc(accUrl)}</span>
          <button class="btn btn-ghost btn-xs" onclick="copyToClipboard('${esc(accUrl)}')">Copy</button>
        </div>`;
      }
      if (bimUrl) {
        urlDisplay.innerHTML += `<div class="detected-url-row">
          <span class="detected-url-badge bim-badge">BIM360</span>
          <span class="detected-url-text" title="${esc(bimUrl)}">${esc(bimUrl)}</span>
          <button class="btn btn-ghost btn-xs" onclick="copyToClipboard('${esc(bimUrl)}')">Copy</button>
        </div>`;
      }
      if (accUrl || bimUrl) urlDisplay.classList.remove('hidden');
    }
  } catch { /* non-fatal */ }
}

function copyToClipboard(text) {
  navigator.clipboard && navigator.clipboard.writeText(text)
    .then(function() { showToast('URL copied.', 'success'); })
    .catch(function() { showToast('Could not copy.', 'warning'); });
}

function onLoginFailed(msg) {
  if (A.loginTimer) clearInterval(A.loginTimer);
  A.loginPending = false; A.loginDetected = false; A.loginStart = null;
  showAuthState('idle');
  updateContinueBtn();
  showToast(`Login failed: ${msg || 'Unknown error'}`, 'error');
}

function updateLoginTimer() {
  if (!A.loginStart) return;
  const elapsed = Math.floor((Date.now() - A.loginStart) / 1000);
  const mins    = Math.floor(elapsed / 60);
  const secs    = elapsed % 60;
  const display = `${mins}:${String(secs).padStart(2, '0')}`;
  setText('wait-timer', display);
}

// ── Platform stats (shown on platform selector page) ─────────────────────────
async function refreshPlatformStats() {
  try {
    const s = await api('/api/status');
    if (s.activeUser !== undefined) { A.activeUser = s.activeUser; updateUserDisplay(); }
    if (s.acc) {
      setText('acc-stat-total',   s.acc.projectCount   || '—');
      setText('acc-stat-pending', s.acc.pendingCount    || '—');
      setText('acc-stat-done',    s.acc.completedCount  || '—');
      // Show BIM360 skipped count if any exist in the ACC project list
      const b360 = s.acc.bim360Count || 0;
      setText('acc-stat-bim360', b360);
      const hasBim360 = b360 > 0;
      const sep  = $('acc-bim360-sep');
      const stat = $('acc-bim360-stat');
      if (sep)  sep.style.display  = hasBim360 ? '' : 'none';
      if (stat) stat.style.display = hasBim360 ? '' : 'none';
    }
    if (s.bim360) {
      setText('bim360-stat-total',   s.bim360.projectCount   || '—');
      setText('bim360-stat-pending', s.bim360.pendingCount    || '—');
      setText('bim360-stat-done',    s.bim360.completedCount  || '—');
    }
    // Discover & Export All button
    const daeBtn = $('btn-discover-export-all');
    if (daeBtn && !A._chainDiscoverPhase) {
      daeBtn.disabled = A.chainRunning || A.exportRunning || A.loginPending || !A.sessionValid;
    }
  } catch { /* ignore */ }
}

function updateUserDisplay() {
  const el   = $('sidebar-user');
  const wrap = $('sidebar-user-wrap');
  if (!el || !wrap) return;
  if (A.activeUser) {
    el.textContent    = A.activeUser;
    wrap.style.display = '';
  } else {
    wrap.style.display = 'none';
  }
}

// ── Projects page ─────────────────────────────────────────────────────────────
async function loadProjects() {
  if (!A.platform) return;
  // Always start with a clean selection slate for the current platform
  A.selectedIds.clear();
  try {
    const r = await api(`/api/${A.platform}/projects`);
    A.projects = r.projects || [];
    renderProjectTable(A.projects);
    updateStats(A.projects);
    updateNavBadge('projects', A.projects.length || '');
    // Show admin URL setup card when no projects are loaded
    showAdminUrlSetup(A.projects.length === 0);
  } catch (e) { showToast(`Failed to load projects: ${e.message}`, 'error'); }
}

function showAdminUrlSetup(show) {
  const card = $('admin-url-setup-card');
  if (card) card.classList.toggle('hidden', !show);
}

async function saveAdminUrl() {
  const input = $('admin-url-input');
  const url   = input ? input.value.trim() : '';
  if (!url) { showToast('Please enter a URL.', 'warning'); return; }
  try {
    await api(`/api/${A.platform}/admin-url`, 'POST', { url });
    showToast('Account URL saved.', 'success');
    showAdminUrlSetup(false);
    await discoverProjects();
  } catch (e) { showToast(`Could not save: ${e.message}`, 'error'); }
}

function renderProjectTable(projects) {
  const tbody = $('projects-tbody');
  const empty = $('no-projects-state');

  if (!projects || !projects.length) {
    showEl('projects-table-wrap', false);
    if (empty) empty.classList.remove('hidden');
    updateStartBtn(); return;
  }
  if (empty) empty.classList.add('hidden');
  showEl('projects-table-wrap', true);

  // Hide the platform <th> — hierarchy is shown via section headers instead
  const thPlatform = $('th-platform');
  if (thPlatform) thPlatform.style.display = 'none';

  // Group a flat project list by hub, preserving discovery order.
  // fallbackName is used when a project has no hubName (e.g. discovered before
  // hub-based API was in use, or coming from a single-account BIM360 workspace).
  function groupByHub(list, fallbackName) {
    const order = [], map = new Map();
    for (const p of list) {
      const key = p.hubId || p.hubName || '__unknown__';
      if (!map.has(key)) {
        const e = { key, name: p.hubName || fallbackName, items: [] };
        map.set(key, e);
        order.push(e);
      }
      map.get(key).items.push(p);
    }
    return order;
  }

  A.selectedIds.clear();
  let hubIdx = 0;

  // sec: 'acc' | 'bim360' — controls row background tinting
  function renderHubGroup(hub, autoSelectPending, sec) {
    const idx   = hubIdx++;
    const hgCls = `hg-${idx}`;
    let projRows = '';
    for (const p of hub.items) {
      const id  = p.id || p.name;
      const sel = autoSelectPending && p.status === 'pending';
      if (sel) A.selectedIds.add(id);
      // 5 <td> to match the 5-column header; col 3 (platform) is always hidden
      projRows += `<tr class="proj-row ${hgCls} proj-sec-${sec}">
        <td class="col-chk"><input type="checkbox" class="pchk" data-id="${esc(id)}" ${sel ? 'checked' : ''} onchange="onCheck(this)"></td>
        <td class="proj-name-cell">${esc(p.name)}</td>
        <td class="proj-plat-cell"></td>
        <td><span class="badge badge-${p.status || 'pending'}">${statusLabel(p.status)}</span></td>
        <td class="col-id mono-id">${esc(p.id || '—')}</td>
      </tr>`;
    }
    const pendingCnt = hub.items.filter(p => p.status === 'pending').length;
    const meta = `${hub.items.length} project${hub.items.length !== 1 ? 's' : ''}${pendingCnt > 0 ? ' · ' + pendingCnt + ' pending' : ''}`;
    return `<tr class="hub-row hub-sec-${sec}" id="hub-row-${idx}" data-hub-idx="${idx}">
      <td class="col-chk"><input type="checkbox" class="hub-chk" id="hub-chk-${idx}" data-hub-idx="${idx}" onchange="onHubCheck(this)"></td>
      <td colspan="4">
        <div class="hub-label">
          <span class="hub-caret" id="hub-caret-${idx}" onclick="toggleHub(${idx})">▾</span>
          <span class="hub-name">${esc(hub.name)}</span>
          <span class="hub-meta">${esc(meta)}</span>
        </div>
      </td>
    </tr>` + projRows;
  }

  let html = '';

  if (A.platform === 'bim360') {
    // BIM360 workspace — all projects belong to this platform; show one section.
    // BIM360 discover doesn't store hub info so fall back to "BIM360 Account".
    html += `<tr class="tree-sec-hdr tree-sec-bim360"><td colspan="5">
      <span class="tree-sec-tag bim-tag">BIM360</span>
      <strong>BIM360 Document Log Projects</strong>
      <span class="tree-sec-count">${projects.length} project${projects.length !== 1 ? 's' : ''}</span>
    </td></tr>`;
    for (const hub of groupByHub(projects, 'BIM360 Account')) {
      html += renderHubGroup(hub, true, 'bim360');
    }
  } else {
    // ACC workspace — split into ACC projects and BIM360 projects found via hub discovery.
    const accProjects = projects.filter(p => (p.platform || 'acc') !== 'bim360');
    const bimProjects = projects.filter(p => p.platform === 'bim360');

    if (accProjects.length > 0) {
      html += `<tr class="tree-sec-hdr tree-sec-acc"><td colspan="5">
        <span class="tree-sec-tag acc-tag">ACC</span>
        <strong>Autodesk Construction Cloud</strong>
        <span class="tree-sec-count">${accProjects.length} project${accProjects.length !== 1 ? 's' : ''}</span>
      </td></tr>`;
      for (const hub of groupByHub(accProjects, 'Account Projects')) {
        html += renderHubGroup(hub, true, 'acc');
      }
    }

    if (bimProjects.length > 0) {
      // Show only a count summary — individual BIM360 project rows are not
      // selectable from the ACC workspace; they must be exported via BIM360.
      html += `<tr class="tree-sec-hdr tree-sec-bim360 bim360-summary-row"><td colspan="5">
        <div class="bim360-summary-inner">
          <div class="bim360-summary-left">
            <span class="tree-sec-tag bim-tag">BIM360</span>
            <strong>${bimProjects.length} BIM360 project${bimProjects.length !== 1 ? 's' : ''} found</strong>
            <span class="tree-sec-note">These projects are exported from the BIM360 workspace.</span>
          </div>
          <button class="btn btn-sm bim-summary-btn" onclick="selectPlatform('bim360')">
            Open BIM360 Workspace
            <svg viewBox="0 0 20 20" fill="currentColor" width="12"><path fill-rule="evenodd" d="M7.293 14.707a1 1 0 010-1.414L10.586 10 7.293 6.707a1 1 0 011.414-1.414l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414 0z"/></svg>
          </button>
        </div>
      </td></tr>`;
    }
  }

  tbody.innerHTML = html;
  document.querySelectorAll('.hub-chk').forEach(syncHubChk);
  syncHeaderCheck();
  updateStartBtn();
}

function toggleHub(idx) {
  const rows   = document.querySelectorAll(`.hg-${idx}`);
  const hubRow = $(`hub-row-${idx}`);
  const caret  = $(`hub-caret-${idx}`);
  const collapsed = hubRow && hubRow.dataset.collapsed === '1';
  rows.forEach(r => r.classList.toggle('hub-row-hidden', !collapsed));
  if (hubRow) hubRow.dataset.collapsed = collapsed ? '' : '1';
  if (caret)  caret.textContent = collapsed ? '▾' : '▸';
}

function onHubCheck(cb) {
  const idx = cb.dataset.hubIdx;
  document.querySelectorAll(`.hg-${idx} .pchk`).forEach(pchk => {
    pchk.checked = cb.checked;
    cb.checked ? A.selectedIds.add(pchk.dataset.id) : A.selectedIds.delete(pchk.dataset.id);
  });
  syncHeaderCheck();
  updateStartBtn();
}

function syncHubChk(hubChk) {
  const idx      = hubChk.dataset.hubIdx;
  const children = [...document.querySelectorAll(`.hg-${idx} .pchk`)];
  if (!children.length) return;
  const n = children.filter(c => c.checked).length;
  hubChk.checked       = n === children.length;
  hubChk.indeterminate = n > 0 && n < children.length;
}

function onCheck(cb) {
  const id = cb.dataset.id;
  cb.checked ? A.selectedIds.add(id) : A.selectedIds.delete(id);
  // Sync the parent hub's checkbox state
  const m = cb.closest('tr.proj-row')?.className.match(/\bhg-(\d+)\b/);
  if (m) syncHubChk($(`hub-chk-${m[1]}`));
  syncHeaderCheck();
  updateStartBtn();
}

function syncHeaderCheck() {
  const all = [...document.querySelectorAll('.pchk')];
  const hdr = $('check-all');
  if (!hdr) return;
  hdr.checked       = all.length > 0 && all.every(c => c.checked);
  hdr.indeterminate = !hdr.checked && all.some(c => c.checked);
  document.querySelectorAll('.hub-chk').forEach(syncHubChk);
}

function updateStats(projects) {
  const exportable = projects.filter(p => p.status !== 'skipped');
  const skipped    = projects.filter(p => p.status === 'skipped');
  const pending    = exportable.filter(p => p.status === 'pending').length;
  const completed  = exportable.filter(p => p.status === 'completed').length;
  const noDm       = exportable.filter(p => p.status === 'no_dm').length;
  setText('stat-total',     exportable.length);
  setText('stat-pending',   pending);
  setText('stat-completed', completed);
  setText('stat-nodm',      noDm);
  // Show BIM360 skipped count in the projects-page stats if any (ACC context only)
  const skipEl = $('stat-bim360-skipped');
  if (skipEl) {
    skipEl.closest?.('[data-skip-row]')?.style && (skipEl.closest('[data-skip-row]').style.display = skipped.length ? '' : 'none');
    setText('stat-bim360-skipped', skipped.length);
  }
}

function updateStartBtn() {
  const btn = $('btn-start-export');
  const cnt = $('selected-count');
  const n   = A.selectedIds.size;
  const thisPlatformRunning = A.platform === 'acc' ? A.accRunning : A.platform === 'bim360' ? A.bim360Running : A.exportRunning;
  // Enable when there are projects to export (selected or pending), not just when selection is non-empty
  const hasProjects = A.projects.length > 0;
  if (btn) btn.disabled = !hasProjects || thisPlatformRunning || A.chainRunning;
  if (cnt) cnt.textContent = n > 0 ? n : '';
}

async function discoverProjects() {
  if (!A.platform) return;
  setDiscoverBusy(true);
  try { await api(`/api/${A.platform}/projects/discover`, 'POST'); }
  catch (e) { showToast(`Discover failed: ${e.message}`, 'error'); setDiscoverBusy(false); }
}

function setDiscoverBusy(on) {
  const btn = $('btn-discover');
  if (!btn) return;
  btn.disabled = on;
  btn.innerHTML = on
    ? '<span class="spinner"></span> Discovering…'
    : '<svg viewBox="0 0 20 20" fill="currentColor" width="14"><path fill-rule="evenodd" d="M8 4a4 4 0 100 8 4 4 0 000-8zM2 8a6 6 0 1110.89 3.476l4.817 4.817a1 1 0 01-1.414 1.414l-4.816-4.816A6 6 0 012 8z"/></svg> Discover Projects';
}

// ── Per-platform discover from the Platforms page ────────────────────────────
async function discoverPlatform(platform) {
  setPlatDiscoverBusy(platform, true);
  try {
    await api(`/api/${platform}/projects/discover`, 'POST');
  } catch (e) {
    showToast(`Discover ${platform.toUpperCase()} failed: ${e.message}`, 'error');
    setPlatDiscoverBusy(platform, false);
  }
}

function setPlatDiscoverBusy(platform, busy) {
  const btn = $(`btn-discover-${platform}`);
  if (!btn) return;
  btn.disabled = busy;
  btn.innerHTML = busy
    ? '<span class="spinner"></span> Discovering…'
    : '<svg viewBox="0 0 20 20" fill="currentColor" width="13"><path fill-rule="evenodd" d="M8 4a4 4 0 100 8 4 4 0 000-8zM2 8a6 6 0 1110.89 3.476l4.817 4.817a1 1 0 01-1.414 1.414l-4.816-4.816A6 6 0 012 8z"/></svg> Discover';
}

async function resetCheckpoint() {
  if (!A.platform) return;
  const isRunning = A.exportRunning && A.runningPlatform === A.platform;
  const msg = isRunning
    ? 'Export is running. Stop and reset all progress for ' + A.platform.toUpperCase() + '? All projects will be re-queued.'
    : 'Reset all progress for ' + A.platform.toUpperCase() + '? All projects will be re-queued for the next run.';
  if (!confirm(msg)) return;

  const btn = $('btn-reset-cp');
  if (btn) { btn.disabled = true; btn.textContent = 'Resetting…'; }
  try {
    await api(`/api/${A.platform}/checkpoint`, 'DELETE');
    // SSE 'checkpoint-reset' event will arrive and update the UI
  } catch (e) {
    showToast('Reset failed: ' + e.message, 'error');
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = 'Reset Progress'; }
  }
}

async function startExport() {
  if (!A.platform) { showToast('Select a platform (ACC or BIM360) first.', 'warning'); navigate('platforms'); return; }
  const fresh = $('export-fresh') && $('export-fresh').checked;
  const ids   = [...A.selectedIds];
  // If nothing explicitly selected, export all (empty projectIds → controller exports all pending)
  try { await api(`/api/${A.platform}/export/start`, 'POST', { projectIds: ids.length ? ids : [], fresh }); }
  catch (e) { showToast(`Could not start export: ${e.message}`, 'error'); }
}

async function exportAll() {
  const fresh = $('export-all-fresh') && $('export-all-fresh').checked;

  // Fetch live pending counts so users can see what will run before confirming
  let accPending = 0, bim360Pending = 0, accUrl = null, bimUrl = null;
  try {
    const s = await api('/api/status');
    accPending    = s.acc?.pendingCount    || 0;
    bim360Pending = s.bim360?.pendingCount || 0;
    accUrl        = s.acc?.accountAdminUrl;
    bimUrl        = s.bim360?.accountAdminUrl;
  } catch {}

  const noneReady = accPending === 0 && bim360Pending === 0;
  const accNotCfg = !accUrl, bimNotCfg = !bimUrl;

  function platformRow(tag, tagClass, label, pending, notCfg) {
    const countColor = pending > 0 ? (tagClass === 'acc-tag' ? 'var(--brand-m)' : 'var(--bim)') : 'var(--txt-3)';
    const statusText = notCfg ? '<em style="color:var(--txt-3);font-size:12px">Not configured</em>'
                              : `<span style="font-weight:700;color:${countColor}">${pending} pending</span>`;
    return `
      <div style="display:flex;align-items:center;justify-content:space-between;padding:12px 16px;
                  background:var(--surface);border-radius:8px;border:1.5px solid var(--border)">
        <div style="display:flex;align-items:center;gap:10px">
          <span class="plat-tag ${tagClass}" style="font-size:10px;padding:2px 7px">${tag}</span>
          <span style="font-weight:600;font-size:14px">${label}</span>
        </div>
        ${statusText}
      </div>`;
  }

  openModal(`
    <h3 style="margin-bottom:8px;font-size:17px;font-weight:800">Export All Platforms</h3>
    <p style="color:var(--txt-2);font-size:13px;margin-bottom:18px">
      All pending projects below will be exported automatically — ACC first, then BIM360.
      Completed and No-DM projects are skipped.
    </p>
    <div style="display:flex;flex-direction:column;gap:8px;margin-bottom:20px">
      ${platformRow('ACC',    'acc-tag', 'Autodesk Construction Cloud', accPending, accNotCfg)}
      ${platformRow('BIM360', 'bim-tag', 'Autodesk BIM 360',           bim360Pending, bimNotCfg)}
    </div>
    ${noneReady ? `<div class="alert alert-warn" style="margin-bottom:18px;font-size:13px">
      No pending projects on either platform. Run <strong>Discover Projects</strong> inside each platform first, or reset the checkpoint.
    </div>` : ''}
    <div style="display:flex;gap:8px;justify-content:flex-end;align-items:center">
      <button class="btn btn-outline btn-sm" onclick="closeModal()">Cancel</button>
      <button class="btn btn-primary btn-sm" id="modal-confirm-export-all"
              ${noneReady ? 'disabled' : ''}
              onclick="closeModal(); _doExportAll(${fresh})">
        <svg viewBox="0 0 20 20" fill="currentColor" width="13"><path fill-rule="evenodd" d="M11.3 1.046A1 1 0 0112 2v5h4a1 1 0 01.82 1.573l-7 10A1 1 0 018 18v-5H4a1 1 0 01-.82-1.573l7-10a1 1 0 011.12-.38z"/></svg>
        Start Export
      </button>
    </div>
  `);
}

async function _doExportAll(fresh) {
  try { await api('/api/export/all', 'POST', { fresh }); }
  catch (e) { showToast(`Could not start export: ${e.message}`, 'error'); }
}

function startNewRun() {
  A.projStatuses = {};
  const list = $('export-project-list');
  if (list) list.innerHTML = '';
  showEl('export-complete-card', false);
  showEl('nodm-retry-section', false);
  navigate('projects');
}

// ── Export controls ───────────────────────────────────────────────────────────
async function pauseExport() {
  const endpoint = A.chainRunning ? '/api/export/all/pause' : `/api/${A.platform}/export/pause`;
  try { await api(endpoint, 'POST'); }
  catch (e) { showToast(`Pause failed: ${e.message}`, 'error'); }
}

async function resumeExport() {
  const endpoint = A.chainRunning ? '/api/export/all/resume' : `/api/${A.platform}/export/resume`;
  try { await api(endpoint, 'POST'); showEl('pause-banner', false); }
  catch (e) { showToast(`Resume failed: ${e.message}`, 'error'); }
}

function updatePauseResumeUI() {
  const ctrls = $('export-run-controls');
  const btnP  = $('btn-pause');
  const btnR  = $('btn-resume');
  if (!ctrls || !btnP || !btnR) return;

  const platformRunning = A.exportRunning && (A.chainRunning || A.runningPlatform === A.platform);
  if (platformRunning && !A.exportPaused) {
    ctrls.classList.remove('hidden');
    showEl('btn-pause',  true);
    showEl('btn-resume', false);
  } else if (platformRunning && A.exportPaused) {
    ctrls.classList.remove('hidden');
    showEl('btn-pause',  false);
    showEl('btn-resume', true);
  } else {
    ctrls.classList.add('hidden');
  }
}

function toggleLogPanel() {
  const body = $('export-body');
  if (!body) return;
  A.logHidden = !A.logHidden;
  body.classList.toggle('log-hidden', A.logHidden);
  // btn-show-log is always visible; update its label based on state
  const showBtn = $('btn-show-log');
  if (showBtn) showBtn.innerHTML = A.logHidden
    ? '<svg viewBox="0 0 20 20" fill="currentColor" width="11"><path fill-rule="evenodd" d="M3 4a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm0 4a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm0 4a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm0 4a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1z"/></svg> Show Logs'
    : '<svg viewBox="0 0 20 20" fill="currentColor" width="11"><path fill-rule="evenodd" d="M3 4a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm0 4a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm0 4a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm0 4a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1z"/></svg> Hide Logs';
}

function syncExportPage() {
  const list = $('export-project-list');
  if (!list) return;
  list.innerHTML = '';
  Object.entries(A.projStatuses).forEach(([id, info]) => {
    upsertPSI(id, info.status, info.name, info.error);
  });
  syncChips(); syncProgress();
  if (A.exportStatus === 'complete') showExportComplete(A.results);
  updatePauseResumeUI();
  if (A.exportPaused) showEl('pause-banner', true);
}

function syncChips() {
  const r = A.results;
  setText('chip-success', r.success || 0);
  setText('chip-failed',  r.failed  || 0);
  setText('chip-nodm',    r.no_dm || r.noDm || 0);
  setText('chip-skipped', r.skipped || 0);
  _syncExportSpinners();
}

function _syncExportSpinners() {
  const running = !!A.exportRunning;
  // Title spinner
  const sp = $('export-spinner');
  if (sp) sp.classList.toggle('hidden', !running);
  // LIVE badge on Projects panel
  const lb = $('live-badge');
  if (lb) lb.classList.toggle('hidden', !running);
  // Pulsing border on stat chips
  document.querySelectorAll('#export-stats-grid .stat-card').forEach(function(c) {
    c.classList.toggle('stat-running', running);
  });
}

function syncProgress() {
  const { completed, total } = A.progress;
  const pct = total > 0 ? Math.round((completed / total) * 100) : 0;
  const fill = $('progress-fill');
  if (fill) fill.style.width = `${pct}%`;
  setText('progress-text', `${completed} of ${total} project${total !== 1 ? 's' : ''}`);
  setText('progress-pct',  `${pct}%`);
}

function setExportTitle(title, sub) {
  setText('export-title',    title);
  setText('export-subtitle', sub);
}

// ── Project status items ──────────────────────────────────────────────────────
function upsertPSI(id, status, name, error) {
  const list = $('export-project-list');
  if (!list) return;
  // Use a simple sanitized key for the element ID — avoid CSS.escape edge cases
  const rowKey    = String(id).replace(/[^a-zA-Z0-9]/g, '_');
  const rowId     = 'psi-' + rowKey;
  let el = document.getElementById(rowId);
  const isNew = !el;
  if (isNew) {
    el = document.createElement('tr');
    el.id = rowId;
    el.setAttribute('data-pid', id);
    list.appendChild(el);
  }
  el.className = 'psi-row' + (status === 'exporting' ? ' psi-row-current' : '');
  const badgeClass = { pending: 'badge-pending', exporting: 'badge-warn', success: 'badge-completed', failed: 'badge-failed', no_dm: 'badge-muted', skipped: 'badge-muted', access_denied: 'badge-failed' }[status] || 'badge-pending';
  const badgeLabel = {
    pending:       '⏳ Pending',
    exporting:     '<span class="spinner-sm" style="width:9px;height:9px;border-width:1.5px"></span> Processing…',
    success:       '✓ Done',
    failed:        '✗ ' + trunc(error || 'Failed', 22),
    no_dm:         '⊘ No DM',
    skipped:       '↷ Skipped',
    access_denied: '⊘ Access Denied',
  }[status] || status;

  // Read files/size from state (more reliable than reading from DOM cells)
  const projState     = A.projStatuses && A.projStatuses[id];
  const existingFiles = (projState && projState.files) ? projState.files : '—';
  const existingSize  = (projState && projState.size)  ? projState.size  : '—';

  el.innerHTML = `
    <td class="psi-name-cell" title="${esc(name)}">${esc(name)}</td>
    <td class="psi-status-cell"><span class="badge ${badgeClass}">${badgeLabel}</span></td>
    <td class="psi-files-cell mono-id">${esc(existingFiles)}</td>
    <td class="psi-size-cell  mono-id">${esc(existingSize)}</td>`;
}

function updatePsiSummary(projectId, totalFiles, totalSizeFormatted) {
  const rowKey = String(projectId).replace(/[^a-zA-Z0-9]/g, '_');
  const row    = document.getElementById('psi-' + rowKey);
  if (!row) return;
  const fc = row.querySelector('.psi-files-cell');
  const sc = row.querySelector('.psi-size-cell');
  if (fc) fc.textContent = totalFiles != null ? Number(totalFiles).toLocaleString() : '—';
  if (sc) sc.textContent = totalSizeFormatted || '—';
}

// ── Active-export visualization card ─────────────────────────────────────────
let _aecStep = 0;
let _aecHideTimer = null;

function aecShow(name, index, total) {
  clearTimeout(_aecHideTimer);
  _aecStep = 1;
  const card   = $('active-export-card');
  const nameEl = $('aec-name');
  const subEl  = $('aec-sub');
  if (!card) return;
  if (nameEl) nameEl.textContent = name   || '—';
  if (subEl)  subEl.textContent  = 'Project ' + index + ' of ' + total + ' — navigating to Data Management…';
  card.classList.remove('hidden');
  _aecSetStep(1);
}

function aecHide() {
  clearTimeout(_aecHideTimer);
  const card = $('active-export-card');
  if (card) card.classList.add('hidden');
  _aecStep = 0;
}

function aecDone(status) {
  _aecSetStep(4, status === 'success' ? 'done' : 'skip');
  clearTimeout(_aecHideTimer);
  _aecHideTimer = setTimeout(aecHide, 1800);
}

function _aecSetStep(step, finalState) {
  _aecStep = step;
  for (let i = 1; i <= 4; i++) {
    const el = $(`aec-step-${i}`);
    if (!el) continue;
    el.classList.remove('aec-step-active', 'aec-step-done');
    if (i < step) el.classList.add('aec-step-done');
    else if (i === step) {
      if (finalState === 'done' || finalState === 'skip') el.classList.add('aec-step-done');
      else el.classList.add('aec-step-active');
    }
  }
}

function aecAdvanceFromLog(msg) {
  if (!msg || _aecStep === 0) return;
  const m   = msg.toLowerCase();
  const sub = $('aec-sub');

  // Step 2 — Export triggered
  if (_aecStep < 2 && (
    m.includes('toolbar detected') || m.includes('files log') ||
    m.includes('export submitted') || m.includes('document log') ||
    m.includes('acc toolbar') || m.includes('bim360 toolbar'))) {
    _aecSetStep(2);
    if (sub) sub.textContent = 'Triggering document log export…';
  }
  // Step 3 — Report capture
  else if (_aecStep < 3 && (
    m.includes('navigating to reports') || m.includes('waiting for report') ||
    m.includes('found report row') || m.includes('opening report'))) {
    _aecSetStep(3);
    if (sub) sub.textContent = 'Waiting for report to generate…';
  }
  // Step 4 — Excel read / done
  else if (_aecStep < 4 && (
    m.includes('excel downloaded') || m.includes('files log summary') ||
    m.includes('total files') || m.includes('done --'))) {
    _aecSetStep(4);
    if (sub) sub.textContent = 'Reading file summary from Excel report…';
  }
}

function showExportComplete(results) {
  const card = $('export-complete-card');
  if (!card) return;
  const failed  = results.failed  || 0;
  const success = results.success || 0;
  const icon    = $('complete-icon');
  if (failed > 0 && success === 0) {
    icon.className = 'complete-icon-wrap ci-error';
    icon.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" width="30" stroke-linecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>`;
    setExportTitle('Export Failed', `${failed} project${failed !== 1 ? 's' : ''} could not be exported.`);
  } else if (failed > 0) {
    icon.className = 'complete-icon-wrap ci-warn';
    icon.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" width="30" stroke-linecap="round"><path d="M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>`;
    setExportTitle('Completed with Errors', `${success} succeeded, ${failed} failed.`);
  } else {
    icon.className = 'complete-icon-wrap ci-success';
    icon.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" width="30" stroke-linecap="round"><polyline points="20 6 9 17 4 12"/></svg>`;
    setExportTitle('Export Complete', `All ${success} project${success !== 1 ? 's' : ''} exported successfully.`);
  }
  setText('cs-success', success);
  setText('cs-failed',  failed);
  setText('cs-emails',  results.emailsQueued || 0);
  setText('cs-skipped', results.skipped || 0);
  card.classList.remove('hidden');
  showNoDmRetrySection();
}

// ── No-DM retry section ───────────────────────────────────────────────────────
function showNoDmRetrySection() {
  const noDmProjects = Object.entries(A.projStatuses)
    .filter(([, info]) => info.status === 'no_dm');
  const section = $('nodm-retry-section');
  if (!section) return;
  if (!noDmProjects.length) { section.classList.add('hidden'); return; }

  setText('nodm-count', noDmProjects.length);
  const tbody = $('nodm-retry-tbody');
  if (tbody) {
    tbody.innerHTML = noDmProjects.map(([id, info]) => `
      <tr>
        <td class="col-chk"><input type="checkbox" class="nodm-cb" data-id="${esc(id)}" checked></td>
        <td>${esc(info.name)}</td>
        <td class="col-id mono-id">${esc(id)}</td>
      </tr>`).join('');
  }
  section.classList.remove('hidden');
}

function toggleAllNoDm(checked) {
  document.querySelectorAll('.nodm-cb').forEach(c => { c.checked = checked; });
}

async function retrySelectedNoDm() {
  const selected = [...document.querySelectorAll('.nodm-cb:checked')].map(c => c.dataset.id);
  if (!selected.length) { showToast('Select at least one project to retry.', 'warning'); return; }
  const btn = $('btn-retry-nodm');
  if (btn) { btn.disabled = true; btn.textContent = 'Retrying…'; }
  try {
    await api(`/api/${A.platform}/checkpoint/reset-projects`, 'POST', { projectIds: selected });
    showEl('nodm-retry-section', false);
    await api(`/api/${A.platform}/export/start`, 'POST', { projectIds: selected });
  } catch (e) {
    showToast(`Retry failed: ${e.message}`, 'error');
    if (btn) { btn.disabled = false; btn.innerHTML = '<svg viewBox="0 0 20 20" fill="currentColor" width="13"><path fill-rule="evenodd" d="M4 2a1 1 0 011 1v2.101a7.002 7.002 0 0111.601 2.566 1 1 0 11-1.885.666A5.002 5.002 0 005.999 7H9a1 1 0 010 2H4a1 1 0 01-1-1V3a1 1 0 011-1zm.008 9.057a1 1 0 011.276.61A5.002 5.002 0 0014.001 13H11a1 1 0 110-2h5a1 1 0 011 1v5a1 1 0 11-2 0v-2.101a7.002 7.002 0 01-11.601-2.566 1 1 0 01.61-1.276z"/></svg> Retry Selected'; }
  }
}

// ── Terminal ──────────────────────────────────────────────────────────────────
function appendLog(entry) {
  const t = $('log-terminal');
  if (!t) return;
  t.appendChild(buildLogEl(entry));
  const as = $('log-autoscroll');
  if (!as || as.checked) t.scrollTop = t.scrollHeight;
}

function replayLogs(entries) {
  const t = $('log-terminal');
  if (!t) return;
  t.innerHTML = '';
  const frag = document.createDocumentFragment();
  entries.forEach(e => frag.appendChild(buildLogEl(e)));
  t.appendChild(frag);
  t.scrollTop = t.scrollHeight;
}

function buildLogEl(entry) {
  const el  = document.createElement('div');
  const lvl = entry.level || 'INFO';
  el.className = `log-row${lvl === 'HEADER' ? ' row-header' : ''}`;
  const ts = entry.timestamp ? new Date(entry.timestamp).toLocaleTimeString('en-US', { hour12: false }) : '';
  el.innerHTML = `<span class="log-lv lv-${lvl}">${lvl.slice(0,4)}</span><span class="log-time">${esc(ts)}</span><span class="log-msg">${esc(entry.message)}</span>`;
  return el;
}

function clearTerminal() { const t = $('log-terminal'); if (t) t.innerHTML = ''; }

// ── Logs page ─────────────────────────────────────────────────────────────────
async function loadLogs() {
  if (!A.platform) return;
  await loadReports();
  if (A.platform === 'bim360') await loadErrorLogs();
  else {
    // ACC doesn't have per-error-log files
    const errTable = $('error-logs-tbody');
    const errNA    = $('error-logs-na');
    if (errTable) errTable.innerHTML = '';
    if (errNA)    errNA.classList.remove('hidden');
    const errNone = $('no-errors-state');
    if (errNone)  errNone.classList.add('hidden');
    setText('error-count', '');
  }
}

async function loadReports() {
  try {
    const r       = await api(`/api/${A.platform}/reports`);
    const reports = r.reports || [];
    const tbody   = $('reports-tbody');
    const empty   = $('no-reports-state');
    if (!reports.length) {
      if (tbody) tbody.innerHTML = '';
      if (empty) empty.classList.remove('hidden');
      return;
    }
    if (empty) empty.classList.add('hidden');
    const bar = $('reports-bulk-bar'); if (bar) bar.classList.remove('hidden');
    tbody.innerHTML = reports.map(rep => `
      <tr>
        <td class="th-check"><input type="checkbox" class="rep-cb" data-filename="${esc(rep.filename)}" onchange="_onRepCbChange()"></td>
        <td style="white-space:nowrap;font-size:12px">${rep.timestamp ? new Date(rep.timestamp).toLocaleString() : rep.filename}</td>
        <td>${rep.total ?? '—'}</td>
        <td class="td-ok">${rep.success ?? '—'}</td>
        <td class="td-err">${rep.failed ?? '—'}</td>
        <td>${rep.no_dm ?? '—'}</td>
        <td>${rep.skipped ?? '—'}</td>
        <td class="td-acc">${rep.emailsQueued ?? rep.emailsSentTotal ?? '—'}</td>
        <td>
          <div style="display:flex;align-items:center;gap:4px">
            <button class="btn btn-ghost btn-xs btn-icon"
                    data-url="/api/${A.platform}/reports/${encodeURIComponent(rep.filename)}/download"
                    data-name="${esc(rep.filename)}"
                    onclick="downloadFile(this.dataset.url,this.dataset.name)"
                    title="Download JSON report">
              <svg viewBox="0 0 20 20" fill="currentColor" width="13"><path fill-rule="evenodd" d="M3 17a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm3.293-7.707a1 1 0 011.414 0L9 10.586V3a1 1 0 112 0v7.586l1.293-1.293a1 1 0 111.414 1.414l-3 3a1 1 0 01-1.414 0l-3-3a1 1 0 010-1.414z" clip-rule="evenodd"/></svg>
            </button>
            <button class="btn btn-ghost btn-xs btn-icon btn-del"
                    data-filename="${esc(rep.filename)}"
                    onclick="deleteReport(this.dataset.filename)"
                    title="Delete report">
              <svg viewBox="0 0 20 20" fill="currentColor" width="13"><path fill-rule="evenodd" d="M9 2a1 1 0 00-.894.553L7.382 4H4a1 1 0 000 2v10a2 2 0 002 2h8a2 2 0 002-2V6a1 1 0 100-2h-3.382l-.724-1.447A1 1 0 0011 2H9zM7 8a1 1 0 012 0v6a1 1 0 11-2 0V8zm5-1a1 1 0 00-1 1v6a1 1 0 102 0V8a1 1 0 00-1-1z" clip-rule="evenodd"/></svg>
            </button>
          </div>
        </td>
      </tr>`).join('');
  } catch (e) { showToast(`Failed to load reports: ${e.message}`, 'error'); }
}

async function loadErrorLogs() {
  try {
    const r    = await api('/api/bim360/logs');
    const logs = r.logs || [];
    const cnt  = $('error-count');
    if (cnt) cnt.textContent = logs.length > 0 ? logs.length : '';
    const tbody = $('error-logs-tbody');
    const empty = $('no-errors-state');
    const na    = $('error-logs-na');
    if (na) na.classList.add('hidden');
    if (!logs.length) {
      if (tbody) tbody.innerHTML = '';
      if (empty) empty.classList.remove('hidden');
      return;
    }
    if (empty) empty.classList.add('hidden');
    const errBar = $('errlogs-bulk-bar'); if (errBar) errBar.classList.remove('hidden');
    tbody.innerHTML = logs.map(log => `
      <tr>
        <td class="th-check"><input type="checkbox" class="errlog-cb" data-filename="${esc(log.filename)}" onchange="_onErrLogCbChange()"></td>
        <td style="font-size:12px;white-space:nowrap">${log.timestamp ? new Date(log.timestamp).toLocaleString() : '—'}</td>
        <td style="font-weight:600">${esc(log.projectName || '—')}</td>
        <td style="color:var(--err);max-width:280px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${esc(log.error || '')}">${esc(log.error || '—')}</td>
        <td>
          <div style="display:flex;align-items:center;gap:4px">
            <button class="btn btn-ghost btn-xs"
                    data-filename="${esc(log.filename)}"
                    onclick="viewErrorDetail(this.dataset.filename)">View</button>
            <button class="btn btn-ghost btn-xs btn-icon"
                    data-url="/api/bim360/logs/${encodeURIComponent(log.filename)}/download"
                    data-name="${esc(log.filename)}"
                    onclick="downloadFile(this.dataset.url,this.dataset.name)"
                    title="Download JSON log">
              <svg viewBox="0 0 20 20" fill="currentColor" width="13"><path fill-rule="evenodd" d="M3 17a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm3.293-7.707a1 1 0 011.414 0L9 10.586V3a1 1 0 112 0v7.586l1.293-1.293a1 1 0 111.414 1.414l-3 3a1 1 0 01-1.414 0l-3-3a1 1 0 010-1.414z" clip-rule="evenodd"/></svg>
            </button>
            <button class="btn btn-ghost btn-xs btn-icon btn-del"
                    data-filename="${esc(log.filename)}"
                    onclick="deleteErrorLog(this.dataset.filename)"
                    title="Delete error log">
              <svg viewBox="0 0 20 20" fill="currentColor" width="13"><path fill-rule="evenodd" d="M9 2a1 1 0 00-.894.553L7.382 4H4a1 1 0 000 2v10a2 2 0 002 2h8a2 2 0 002-2V6a1 1 0 100-2h-3.382l-.724-1.447A1 1 0 0011 2H9zM7 8a1 1 0 012 0v6a1 1 0 11-2 0V8zm5-1a1 1 0 00-1 1v6a1 1 0 102 0V8a1 1 0 00-1-1z" clip-rule="evenodd"/></svg>
            </button>
          </div>
        </td>
      </tr>`).join('');
  } catch (e) { showToast(`Failed to load error logs: ${e.message}`, 'error'); }
}

async function viewErrorDetail(filename) {
  try {
    const log = await api(`/api/bim360/logs/${encodeURIComponent(filename)}`);
    openModal(`
      <h3 style="margin-bottom:20px;font-size:17px;font-weight:800">Error Detail</h3>
      <dl style="display:grid;grid-template-columns:120px 1fr;gap:12px 16px;font-size:13px;align-items:start">
        <dt style="color:var(--txt-3);font-weight:600">Project</dt>      <dd style="font-weight:600">${esc(log.projectName||'—')}</dd>
        <dt style="color:var(--txt-3);font-weight:600">Project ID</dt>   <dd class="mono-id">${esc(log.projectId||'—')}</dd>
        <dt style="color:var(--txt-3);font-weight:600">Timestamp</dt>    <dd>${log.timestamp ? new Date(log.timestamp).toLocaleString() : '—'}</dd>
        <dt style="color:var(--txt-3);font-weight:600">Error</dt>        <dd style="color:var(--err);word-break:break-word">${esc(log.error||'—')}</dd>
        ${log.screenshotPath ? `<dt style="color:var(--txt-3);font-weight:600">Screenshot</dt><dd class="mono-id" style="word-break:break-all">${esc(log.screenshotPath)}</dd>` : ''}
      </dl>`);
  } catch (e) { showToast(`Could not load log: ${e.message}`, 'error'); }
}

function deleteReport(filename) {
  openModal(`
    <h3 style="margin-bottom:12px;font-size:16px;font-weight:800">Delete Report?</h3>
    <p style="color:var(--txt-2);font-size:13px;margin-bottom:8px">This action cannot be undone.</p>
    <p class="mono-id" style="margin-bottom:24px;word-break:break-all">${esc(filename)}</p>
    <div style="display:flex;gap:8px;justify-content:flex-end">
      <button class="btn btn-outline btn-sm" onclick="closeModal()">Cancel</button>
      <button class="btn btn-sm" style="background:var(--err);color:#fff;border-color:var(--err)"
              data-filename="${esc(filename)}"
              onclick="closeModal();_doDeleteReport(this.dataset.filename)">Delete</button>
    </div>`);
}

async function downloadFile(url, filename) {
  try {
    const res = await fetch(url);
    if (!res.ok) {
      let msg = `HTTP ${res.status}`;
      try { const d = await res.json(); msg = d.error || msg; } catch {}
      showToast(`Download failed: ${msg}`, 'error');
      return;
    }
    const blob = await res.blob();
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(a.href);
  } catch (e) { showToast(`Download failed: ${e.message}`, 'error'); }
}

async function _doDeleteReport(filename) {
  try {
    await api(`/api/${A.platform}/reports/${encodeURIComponent(filename)}`, 'DELETE');
    showToast('Report deleted.', 'success');
    await loadReports();
  } catch (e) { showToast(`Delete failed: ${e.message}`, 'error'); }
}

function deleteErrorLog(filename) {
  openModal(`
    <h3 style="margin-bottom:12px;font-size:16px;font-weight:800">Delete Error Log?</h3>
    <p style="color:var(--txt-2);font-size:13px;margin-bottom:8px">This action cannot be undone.</p>
    <p class="mono-id" style="margin-bottom:24px;word-break:break-all">${esc(filename)}</p>
    <div style="display:flex;gap:8px;justify-content:flex-end">
      <button class="btn btn-outline btn-sm" onclick="closeModal()">Cancel</button>
      <button class="btn btn-sm" style="background:var(--err);color:#fff;border-color:var(--err)"
              data-filename="${esc(filename)}"
              onclick="closeModal();_doDeleteErrorLog(this.dataset.filename)">Delete</button>
    </div>`);
}

async function _doDeleteErrorLog(filename) {
  try {
    await api(`/api/bim360/logs/${encodeURIComponent(filename)}`, 'DELETE');
    showToast('Error log deleted.', 'success');
    await loadErrorLogs();
  } catch (e) { showToast(`Delete failed: ${e.message}`, 'error'); }
}

// ── Bulk-select helpers: Reports ─────────────────────────────────────────────
function _onRepCbChange() {
  const all = [...document.querySelectorAll('.rep-cb')];
  const checked = all.filter(c => c.checked);
  const btn = $('btn-delete-reports');
  const cnt = $('reports-sel-count');
  if (btn) btn.disabled = checked.length === 0;
  if (cnt) cnt.textContent = checked.length ? `${checked.length} selected` : '';
  const hdr1 = $('reports-select-all'); const hdr2 = $('reports-select-all-hdr');
  const allChecked = checked.length === all.length && all.length > 0;
  if (hdr1) hdr1.checked = allChecked; if (hdr2) hdr2.checked = allChecked;
}

function toggleSelectAllReports(checked) {
  document.querySelectorAll('.rep-cb').forEach(c => { c.checked = checked; });
  const hdr1 = $('reports-select-all'); const hdr2 = $('reports-select-all-hdr');
  if (hdr1) hdr1.checked = checked; if (hdr2) hdr2.checked = checked;
  _onRepCbChange();
}

async function deleteSelectedReports() {
  const selected = [...document.querySelectorAll('.rep-cb:checked')].map(c => c.dataset.filename);
  if (!selected.length) return;
  openModal(`
    <h3 style="margin-bottom:12px;font-size:16px;font-weight:800">Delete ${selected.length} Report${selected.length > 1 ? 's' : ''}?</h3>
    <p style="color:var(--txt-2);font-size:13px;margin-bottom:24px">This action cannot be undone.</p>
    <div style="display:flex;gap:8px;justify-content:flex-end">
      <button class="btn btn-outline btn-sm" onclick="closeModal()">Cancel</button>
      <button class="btn btn-sm" style="background:var(--err);color:#fff;border-color:var(--err)"
              onclick="closeModal();_doBulkDeleteReports()">Delete</button>
    </div>`);
}

async function _doBulkDeleteReports() {
  const selected = [...document.querySelectorAll('.rep-cb:checked')].map(c => c.dataset.filename);
  let failed = 0;
  for (const f of selected) {
    try { await api(`/api/${A.platform}/reports/${encodeURIComponent(f)}`, 'DELETE'); }
    catch { failed++; }
  }
  if (failed) showToast(`${failed} deletion(s) failed.`, 'error');
  else showToast(`${selected.length} report${selected.length > 1 ? 's' : ''} deleted.`, 'success');
  await loadReports();
}

// ── Bulk-select helpers: Error Logs ──────────────────────────────────────────
function _onErrLogCbChange() {
  const all = [...document.querySelectorAll('.errlog-cb')];
  const checked = all.filter(c => c.checked);
  const btn = $('btn-delete-errlogs');
  const cnt = $('errlogs-sel-count');
  if (btn) btn.disabled = checked.length === 0;
  if (cnt) cnt.textContent = checked.length ? `${checked.length} selected` : '';
  const hdr1 = $('errlogs-select-all'); const hdr2 = $('errlogs-select-all-hdr');
  const allChecked = checked.length === all.length && all.length > 0;
  if (hdr1) hdr1.checked = allChecked; if (hdr2) hdr2.checked = allChecked;
}

function toggleSelectAllErrLogs(checked) {
  document.querySelectorAll('.errlog-cb').forEach(c => { c.checked = checked; });
  const hdr1 = $('errlogs-select-all'); const hdr2 = $('errlogs-select-all-hdr');
  if (hdr1) hdr1.checked = checked; if (hdr2) hdr2.checked = checked;
  _onErrLogCbChange();
}

async function deleteSelectedErrLogs() {
  const selected = [...document.querySelectorAll('.errlog-cb:checked')].map(c => c.dataset.filename);
  if (!selected.length) return;
  openModal(`
    <h3 style="margin-bottom:12px;font-size:16px;font-weight:800">Delete ${selected.length} Log${selected.length > 1 ? 's' : ''}?</h3>
    <p style="color:var(--txt-2);font-size:13px;margin-bottom:24px">This action cannot be undone.</p>
    <div style="display:flex;gap:8px;justify-content:flex-end">
      <button class="btn btn-outline btn-sm" onclick="closeModal()">Cancel</button>
      <button class="btn btn-sm" style="background:var(--err);color:#fff;border-color:var(--err)"
              onclick="closeModal();_doBulkDeleteErrLogs()">Delete</button>
    </div>`);
}

async function _doBulkDeleteErrLogs() {
  const selected = [...document.querySelectorAll('.errlog-cb:checked')].map(c => c.dataset.filename);
  let failed = 0;
  for (const f of selected) {
    try { await api(`/api/bim360/logs/${encodeURIComponent(f)}`, 'DELETE'); }
    catch { failed++; }
  }
  if (failed) showToast(`${failed} deletion(s) failed.`, 'error');
  else showToast(`${selected.length} log${selected.length > 1 ? 's' : ''} deleted.`, 'success');
  await loadErrorLogs();
}

// ── Tabs ──────────────────────────────────────────────────────────────────────
function switchTab(id) {
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
  document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
  const btn = document.querySelector(`.tab-btn[data-tab="${id}"]`);
  const pnl = $(`tab-${id}`);
  if (btn) btn.classList.add('active');
  if (pnl) pnl.classList.add('active');
}

// ── Nav badges ────────────────────────────────────────────────────────────────
function updateNavBadge(page, val) {
  const el = $(`nav-badge-${page}`);
  if (!el) return;
  el.textContent   = val;
  el.style.display = val ? '' : 'none';
}
function navBadgeExport(on) {
  const el = $('nav-badge-export');
  if (el) el.classList.toggle('hidden', !on);
}

// ── Modal ─────────────────────────────────────────────────────────────────────
function openModal(html) { $('modal-content').innerHTML = html; $('modal').classList.remove('hidden'); }
function closeModal()    { $('modal').classList.add('hidden'); }

// ── Toast ─────────────────────────────────────────────────────────────────────
let toastTimer;
function showToast(msg, type = 'info') {
  const t = $('toast');
  clearTimeout(toastTimer);
  t.className = `toast toast-${type}`;
  t.textContent = msg;
  toastTimer = setTimeout(() => t.classList.add('hidden'), 4500);
}

// ── API ───────────────────────────────────────────────────────────────────────
async function api(url, method = 'GET', body) {
  const opts = { method, headers: { 'Content-Type': 'application/json' } };
  if (body) opts.body = JSON.stringify(body);
  const res  = await fetch(url, opts);
  let data = {};
  try { data = await res.json(); } catch (_) { /* empty or non-JSON body */ }
  if (!res.ok) throw new Error(data.error || data.message || `HTTP ${res.status}`);
  return data;
}

// ── DOM helpers ───────────────────────────────────────────────────────────────
const $    = id  => document.getElementById(id);
const esc  = s   => String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
const setText = (id, v)   => { const e = $(id); if (e) e.textContent = v; };
const showEl  = (id, vis) => { const e = $(id); if (e) e.classList.toggle('hidden', !vis); };
const trunc   = (s, n)    => s && s.length > n ? s.slice(0, n) + '…' : (s || '');
function statusLabel(s) {
  return { pending: '⏳ Pending', completed: '✓ Completed', no_dm: '⊘ No DM', failed: '✗ Failed', skipped: '⊘ Skipped' }[s] || (s || 'Unknown');
}

// ── Cloudsfer App Auth ────────────────────────────────────────────────────────
async function checkAuth() {
  try {
    const res  = await fetch('/api/auth/me');
    const data = await res.json();
    if (data.authenticated) {
      A.appUser = data.email;
      // Hide overlay and start SSE — don't call navigate() here so the
      // welcome page (default active page) shows without an extra navigate flash
      const overlay = $('auth-overlay');
      if (overlay) overlay.style.display = 'none';
      connectSSE();
    } else {
      _showAuthOverlay();
    }
  } catch {
    _showAuthOverlay();
  }
}

function _showAuthOverlay() {
  // Clear every input field so no credentials persist between users
  ['signin-email', 'signin-password', 'create-email', 'create-password', 'create-confirm']
    .forEach(id => { const el = $(id); if (el) el.value = ''; });
  // Hide all messages and reset to sign-in tab
  ['signin-error', 'signin-success', 'create-error'].forEach(id => {
    const el = $(id); if (el) el.classList.add('hidden');
  });
  showAuthTab('signin');
  const overlay = $('auth-overlay');
  if (overlay) overlay.style.display = 'flex';
}

function _onAuthSuccess() {
  const overlay = $('auth-overlay');
  if (overlay) overlay.style.display = 'none';
  // Reset all state so previous user's data doesn't bleed through
  A.activeUser = null;
  A.platform   = null;
  A.projects   = [];
  A.selectedIds.clear();
  connectSSE();
  navigate('newpage');
}

function showAuthTab(tab) {
  const isSignin = tab === 'signin';
  $('form-signin').classList.toggle('hidden', !isSignin);
  $('form-create').classList.toggle('hidden', isSignin);
  $('tab-signin').classList.toggle('active', isSignin);
  $('tab-create').classList.toggle('active', !isSignin);
  ['signin-error', 'signin-success', 'create-error'].forEach(id => {
    const el = $(id); if (el) el.classList.add('hidden');
  });
}

async function handleLogin(e) {
  e.preventDefault();
  const email    = $('signin-email').value.trim();
  const password = $('signin-password').value;
  const errEl    = $('signin-error');
  const btn      = $('btn-signin-submit');
  errEl.classList.add('hidden');
  btn.disabled = true;
  btn.textContent = 'Signing in…';
  try {
    const res  = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    const data = await res.json();
    if (res.ok) {
      A.appUser = data.email;
      _onAuthSuccess();
    } else {
      errEl.textContent = data.error || 'Login failed.';
      errEl.classList.remove('hidden');
    }
  } catch {
    errEl.textContent = 'Network error — please try again.';
    errEl.classList.remove('hidden');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Sign In';
  }
}

async function handleSignup(e) {
  e.preventDefault();
  const email    = $('create-email').value.trim();
  const password = $('create-password').value;
  const confirm  = $('create-confirm').value;
  const errEl    = $('create-error');
  const btn      = $('btn-create-submit');
  errEl.classList.add('hidden');
  if (password !== confirm) {
    errEl.textContent = 'Passwords do not match.';
    errEl.classList.remove('hidden');
    return;
  }
  btn.disabled = true;
  btn.textContent = 'Creating account…';
  try {
    const res  = await fetch('/api/auth/signup', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    const data = await res.json();
    if (res.ok) {
      // Account created — redirect to Sign In with pre-filled email
      const email = $('create-email').value.trim();
      showAuthTab('signin');
      const emailEl = $('signin-email');
      if (emailEl) emailEl.value = email;
      const successEl = $('signin-success');
      if (successEl) {
        successEl.textContent = 'Account created! Please sign in below.';
        successEl.classList.remove('hidden');
      }
    } else {
      errEl.textContent = data.error || 'Account creation failed.';
      errEl.classList.remove('hidden');
    }
  } catch {
    errEl.textContent = 'Network error — please try again.';
    errEl.classList.remove('hidden');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Create Account';
  }
}

async function logout() {
  try { await fetch('/api/auth/logout', { method: 'POST' }); } catch {}
  if (sse) { sse.close(); sse = null; }
  A.activeUser = null;
  A.appUser    = null;
  navigate('newpage');
  _showAuthOverlay();
  showAuthTab('signin');
}

// ── Boot ──────────────────────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {

  // Sidebar navigation — also closes the drawer on mobile
  document.querySelectorAll('.nav-item').forEach(n => {
    n.addEventListener('click', () => { navigate(n.dataset.page); closeSidebar(); });
  });

    // Auth page buttons
    $('btn-login').addEventListener('click', startLogin);
    $('btn-relogin').addEventListener('click', startLogin);
    $('btn-to-platforms').addEventListener('click', () => navigate('platforms'));
    $('btn-cancel-login') && $('btn-cancel-login').addEventListener('click', () => {
        // User cancels — just go back to idle
        if (A.loginTimer) clearInterval(A.loginTimer);
        A.loginPending = false; A.loginStart = null;
        showAuthState('idle');
        showToast('Login cancelled.', 'warning');
    });

  // Projects page — header checkbox & bulk actions
  $('check-all').addEventListener('change', e => {
    document.querySelectorAll('.pchk').forEach(cb => {
      cb.checked = e.target.checked;
      e.target.checked ? A.selectedIds.add(cb.dataset.id) : A.selectedIds.delete(cb.dataset.id);
    });
    document.querySelectorAll('.hub-chk').forEach(syncHubChk);
    syncHeaderCheck(); updateStartBtn();
  });
  $('btn-select-all').addEventListener('click', () => {
    document.querySelectorAll('.pchk').forEach(cb => { cb.checked = true; A.selectedIds.add(cb.dataset.id); });
    document.querySelectorAll('.hub-chk').forEach(syncHubChk);
    syncHeaderCheck(); updateStartBtn();
  });
  $('btn-select-pending').addEventListener('click', () => {
    A.selectedIds.clear();
    document.querySelectorAll('.pchk').forEach(cb => {
      const isPending = cb.closest('tr')?.querySelector('.badge-pending');
      cb.checked = !!isPending;
      if (isPending) A.selectedIds.add(cb.dataset.id);
    });
    document.querySelectorAll('.hub-chk').forEach(syncHubChk);
    syncHeaderCheck(); updateStartBtn();
  });
  $('btn-deselect-all').addEventListener('click', () => {
    A.selectedIds.clear();
    document.querySelectorAll('.pchk').forEach(cb => { cb.checked = false; });
    document.querySelectorAll('.hub-chk').forEach(syncHubChk);
    syncHeaderCheck(); updateStartBtn();
  });
  $('btn-reset-cp')    .addEventListener('click', resetCheckpoint);
  $('btn-discover')    .addEventListener('click', discoverProjects);
  $('btn-start-export').addEventListener('click', startExport);

  // Export page
  $('btn-pause')    .addEventListener('click', pauseExport);
  $('btn-resume')   .addEventListener('click', resumeExport);
  $('btn-clear-log').addEventListener('click', () => { clearTerminal(); A.logs = []; });

  // Logs page
  $('btn-refresh-logs').addEventListener('click', loadLogs);
  document.querySelectorAll('.tab-btn').forEach(b => {
    b.addEventListener('click', () => switchTab(b.dataset.tab));
  });

  // Keyboard shortcuts
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') closeModal();
  });

  // Apply initial page state so wizard-fullscreen class and stepper are set before checkAuth
  navigate(A.page);

  // Check Cloudsfer app auth — starts SSE only if authenticated
  checkAuth();
});

/* ===================================================================
   NEW PAGE — 5-step wizard  (np_ namespace)
   =================================================================== */
const NP = {
  step:1, platform:'both',
  projects:[], selectedIds:new Set(), filterTab:'all',
  loginStart:null, loginTimer:null,
  export:{running:false,completed:0,total:0,noDm:0,success:0,accessDenied:0},
  _multiPlatform:false, _pendingPlatforms:0,
};

function npGoStep(n){
  NP.step=n;
  for(let i=1;i<=5;i++){
    const pg=document.getElementById('np-page-'+i);
    if(pg){pg.classList.toggle('np-page-active',i===n);pg.classList.toggle('hidden',i!==n);}
    const sw=document.getElementById('np-s'+i);
    if(sw){sw.classList.remove('np-active','np-done');if(i===n)sw.classList.add('np-active');if(i<n)sw.classList.add('np-done');}
    const ln=document.getElementById('np-l'+i);
    if(ln)ln.classList.toggle('np-done',i<n);
  }
  if(n===3)npLoadProjects();
  if(n===4)npOnEnterExport();
  if(n===5)npOnEnterResults();
}

function npShowIdle(){
  document.getElementById('np-auth-idle').classList.remove('hidden');
  document.getElementById('np-auth-waiting').classList.add('hidden');
  document.getElementById('np-auth-success').classList.add('hidden');
}
function npShowWaiting(){
  document.getElementById('np-auth-idle').classList.add('hidden');
  document.getElementById('np-auth-waiting').classList.remove('hidden');
  document.getElementById('np-auth-success').classList.add('hidden');
}
function npShowSuccess(user){
  document.getElementById('np-auth-idle').classList.add('hidden');
  document.getElementById('np-auth-waiting').classList.add('hidden');
  const box=document.getElementById('np-auth-success');
  box.classList.remove('hidden');
  const u=user?'Signed in as <strong>'+String(user).replace(/</g,'&lt;')+'</strong>':'Successfully connected to Autodesk';
  box.innerHTML=
    '<div style="width:56px;height:56px;border-radius:50%;background:#dcfce7;border:3px solid #86efac;display:flex;align-items:center;justify-content:center;margin-bottom:16px">'
    +'<svg viewBox="0 0 20 20" fill="currentColor" width="28" style="color:#166534"><path fill-rule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"/></svg></div>'
    +'<h2 class="np-auth-h2">Authentication Complete</h2>'
    +'<p class="np-auth-p">'+u+'</p>'
    +'<div style="display:flex;gap:10px;margin-top:8px;width:100%">'
    +'  <button class="np-btn-primary" onclick="npGoStep(2)" style="flex:1">Choose Platform →</button>'
    +'  <button class="np-btn-primary" onclick="npShowIdle()" style="flex:0 0 auto">'
    +'    <svg viewBox="0 0 20 20" fill="currentColor" width="13"><path fill-rule="evenodd" d="M4 2a1 1 0 011 1v2.101a7.002 7.002 0 0111.601 2.566 1 1 0 11-1.885.666A5.002 5.002 0 005.999 7H9a1 1 0 010 2H4a1 1 0 01-1-1V3a1 1 0 011-1zm.008 9.057a1 1 0 011.276.61A5.002 5.002 0 0014.001 13H11a1 1 0 110-2h5a1 1 0 011 1v5a1 1 0 11-2 0v-2.101a7.002 7.002 0 01-11.601-2.566 1 1 0 01.61-1.276z"/></svg>'
    +'    Re-authenticate'
    +'  </button>'
    +'</div>';
}

async function npStartLogin(){
  NP.loginStart=Date.now();npShowWaiting();npStartTimer();
  try{await fetch('/api/login/start',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});}
  catch(e){npClearTimer();npShowIdle();if(typeof showToast!=='undefined')showToast('Could not start login: '+e.message,'error');}
}
function npCancelLogin(){npClearTimer();npShowIdle();fetch('/api/login/cancel',{method:'POST'}).catch(function(){});}
function npStartTimer(){
  npClearTimer();
  NP.loginTimer=setInterval(function(){
    var el=document.getElementById('np-timer');if(!el||!NP.loginStart)return;
    var s=Math.floor((Date.now()-NP.loginStart)/1000);
    el.textContent=Math.floor(s/60)+':'+String(s%60).padStart(2,'0');
  },1000);
}
function npClearTimer(){if(NP.loginTimer){clearInterval(NP.loginTimer);NP.loginTimer=null;}}

// ── Wizard (NP) sync functions — called directly from handleEvent ─────────────
function npSyncProgress(data) {
  var r = data.results || {};
  // Always take the LARGER of current total and platform batch total.
  // Prevents a per-platform progress-update from shrinking the multi-platform total
  // that npOnEnterExport set from NP.selectedIds.size (keeps 4, ignores per-platform 2).
  NP.export.total = Math.max(NP.export.total || 0, data.total || 0);

  // In multi-platform runs each progress-update only carries ONE platform's counters.
  // npSyncProjectDone already tracks completed/success/noDm cumulatively, so
  // overwriting here would reset the cross-platform totals to a single platform's values.
  if (!NP._multiPlatform) {
    NP.export.completed = data.completed || 0;
    NP.export.success   = r.success || r.Success || 0;
    var serverNoDm = r.no_dm || r.noDm || r.NoDm || 0;
    if (serverNoDm > (NP.export.noDm || 0)) {
      NP.export.noDm = serverNoDm;
      _npSetNoDm(NP.export.noDm);
    }
  }
  if (NP.step === 4 && typeof npUpdateExport === 'function') npUpdateExport();
}

function npSyncProjectDone(data) {
  var status = data.status;

  NP.export.completed = (NP.export.completed || 0) + 1;
  if (status === 'success') NP.export.success = (NP.export.success || 0) + 1;
  if (status === 'no_dm') {
    NP.export.noDm = (NP.export.noDm || 0) + 1;
    console.log('[NP] no_dm project done, noDm count now:', NP.export.noDm);
    _npSetNoDm(NP.export.noDm);
  }
  if (status === 'access_denied') {
    NP.export.accessDenied = (NP.export.accessDenied || 0) + 1;
  }

  var pid  = data.project && (data.project.id || data.project.name);
  var proj = pid && NP.projects.find(function(p) { return p.id === pid || p.name === pid; });
  if (proj) {
    proj.files     = (data.totalFiles > 0) ? Number(data.totalFiles).toLocaleString() : '—';
    proj.size      = data.totalSizeFormatted || '—';
    proj.sizeBytes = data.totalSizeBytes || 0;
    proj.status    = status;
  }

  if (NP.step === 4 && typeof npUpdateExport === 'function') npUpdateExport();
  if (NP.step === 5 && typeof npRenderResults === 'function') npRenderResults();
}

// Dedicated helper — updates every element that shows the no_dm count
function _npSetNoDm(count) {
  var val = String(count || 0);
  // querySelectorAll so duplicate IDs (if any) are all updated
  document.querySelectorAll('#np-nodm').forEach(function(el) {
    el.textContent = val;
    console.log('[NP] set #np-nodm to', val, el);
  });
  // Also cover the badge on the EXEMPT card directly
  document.querySelectorAll('[data-nodm-badge]').forEach(function(el) {
    el.textContent = val;
  });
}

function npSyncComplete() {
  // Track how many platforms have completed in a multi-platform wizard run
  if (NP._multiPlatform) {
    NP._pendingPlatforms = Math.max(0, (NP._pendingPlatforms || 1) - 1);
    if (NP._pendingPlatforms > 0) {
      // Other platform still running — update progress but don't mark overall done yet
      if (typeof npUpdateExport === 'function') npUpdateExport();
      return;
    }
    NP._multiPlatform = false;
  }
  if (NP.step !== 4) return;
  NP.export.running = false;
  if (typeof npUpdateExport === 'function') npUpdateExport();
  var bf = document.getElementById('np-btn-finalize');
  if (bf) bf.disabled = false;
  if (typeof npSetOverall === 'function') npSetOverall(100, 'COMPLETE');
}

function npSyncFileSummary(projectId, files, size) {
  var proj = NP.projects.find(function(p) { return p.id === projectId || p.name === projectId; });
  if (proj) { proj.files = files; proj.size = size; }
  if (NP.step === 5 && typeof npRenderResults === 'function') npRenderResults();
}

function npPickPlat(p){
  NP.platform=p;
  document.querySelectorAll('.np-plat-opt').forEach(function(el){
    var sel=el.dataset.plat===p;el.classList.toggle('np-plat-sel',sel);
    var chk=document.getElementById('np-chk-'+el.dataset.plat);if(chk)chk.classList.toggle('hidden',!sel);
  });
}

async function npLoadProjects(){
  try{
    var results=await Promise.all([
      fetch('/api/acc/projects').then(function(r){return r.json();}).catch(function(){return{projects:[],adminUrl:'',adminUrlConfigured:false};}),
      fetch('/api/bim360/projects').then(function(r){return r.json();}).catch(function(){return{projects:[],adminUrl:'',adminUrlConfigured:false};})
    ]);
    var accData=results[0],bimData=results[1];
    var banner=document.getElementById('np-admin-banner'),inp=document.getElementById('np-url-input');
    if(banner)banner.classList.toggle('hidden',!!(accData.adminUrlConfigured||bimData.adminUrlConfigured));
    if(inp&&!inp.value)inp.value=accData.adminUrl||bimData.adminUrl||'';
    var seen=new Set();NP.projects=[];
    (accData.projects||[]).forEach(function(p){var q=Object.assign({},p,{platform:p.rawPlatform==='bim360'?'bim360':'acc'});if(!seen.has(q.id)){seen.add(q.id);NP.projects.push(q);}});
    (bimData.projects||[]).forEach(function(p){var q=Object.assign({},p,{platform:'bim360'});if(!seen.has(q.id)){seen.add(q.id);NP.projects.push(q);}});
    npRenderTable();
  }catch(e){if(typeof showToast!=='undefined')showToast('Failed to load: '+e.message,'error');}
}
async function npSaveAndDiscover(){
  var inp=document.getElementById('np-url-input');
  var url=(inp?inp.value:'').trim();
  if(!url){if(typeof showToast!=='undefined')showToast('Please enter a URL.','warning');return;}
  var isAcc=url.indexOf('acc.autodesk.com')>=0,isBim=url.indexOf('b360.autodesk.com')>=0;
  var plat=isAcc?'acc':isBim?'bim360':'acc';
  try{
    await fetch('/api/'+plat+'/admin-url',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({url:url})});
    if(typeof showToast!=='undefined')showToast('URL saved — discovering...','success');
    if(NP.platform!=='bim360')fetch('/api/acc/projects/discover',{method:'POST'}).catch(function(){});
    if(NP.platform!=='acc')fetch('/api/bim360/projects/discover',{method:'POST'}).catch(function(){});
  }catch(e){if(typeof showToast!=='undefined')showToast('Error: '+e.message,'error');}
}
function npSetTab(btn,tab){
  NP.filterTab=tab;
  document.querySelectorAll('.np-ftab').forEach(function(b){b.classList.toggle('np-ftab-active',b.dataset.tab===tab);});
  npRenderTable();
}
function npRenderTable(){
  var tbody=document.getElementById('np-tbody'),empty=document.getElementById('np-empty');
  var search=(document.getElementById('np-proj-search')?document.getElementById('np-proj-search').value:'').toLowerCase();
  var list=NP.projects.filter(function(p){
    if(NP.filterTab==='bim360'&&p.platform!=='bim360')return false;
    if(NP.filterTab==='acc'&&p.platform!=='acc')return false;
    if(search&&p.name.toLowerCase().indexOf(search)<0)return false;
    return true;
  });
  if(!list.length){if(tbody)tbody.innerHTML='';if(empty)empty.classList.remove('hidden');npUpdateSel();return;}
  if(empty)empty.classList.add('hidden');
  var e2=function(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');};
  if(tbody)tbody.innerHTML=list.map(function(p){
    var badge=p.platform==='bim360'?'<span class="np-badge-bim360">BIM360</span>':'<span class="np-badge-acc">ACC</span>';
    var last=p.status==='completed'?'Previously exported':'Never';
    return '<tr><td style="text-align:center"><input type="checkbox" '+(NP.selectedIds.has(p.id)?'checked':'')+' data-id="'+e2(p.id)+'" onchange="npCheck(this)"></td><td>'+e2(p.name)+'</td><td>'+badge+'</td><td style="font-size:13px;color:#64748b">'+last+'</td></tr>';
  }).join('');
  npUpdateSel();
}
function npCheck(cb){if(cb.checked)NP.selectedIds.add(cb.dataset.id);else NP.selectedIds.delete(cb.dataset.id);npUpdateSel();}
function npToggleAll(checked){
  NP.projects.filter(function(p){
    if(NP.filterTab==='bim360'&&p.platform!=='bim360')return false;
    if(NP.filterTab==='acc'&&p.platform!=='acc')return false;return true;
  }).forEach(function(p){if(checked)NP.selectedIds.add(p.id);else NP.selectedIds.delete(p.id);});
  npRenderTable();
}
function npUpdateSel(){
  var n=NP.selectedIds.size,el=document.getElementById('np-sel-count');
  if(el)el.textContent=n+' project'+(n!==1?'s':'')+' selected';
  var btn=document.getElementById('np-btn-export');if(btn)btn.disabled=n===0;
}
async function npStartExport(){
  if(!NP.selectedIds.size){if(typeof showToast!=='undefined')showToast('Select at least one project.','warning');return;}
  NP._startingExport=true;
  npGoStep(4);
  var ids=Array.from(NP.selectedIds);
  var accIds=NP.projects.filter(function(p){return p.platform!=='bim360'&&ids.indexOf(p.id)>=0;}).map(function(p){return p.id;});
  var bimIds=NP.projects.filter(function(p){return p.platform==='bim360'&&ids.indexOf(p.id)>=0;}).map(function(p){return p.id;});
  var startingAcc=(accIds.length&&NP.platform!=='bim360');
  var startingBim=(bimIds.length&&NP.platform!=='acc');
  // Flag multi-platform run so export-start doesn't wipe the first platform's data
  NP._multiPlatform = !!(startingAcc && startingBim);
  NP._pendingPlatforms = (startingAcc?1:0)+(startingBim?1:0);
  try{
    if(startingAcc)await fetch('/api/acc/export/start',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectIds:accIds})});
    if(startingBim)await fetch('/api/bim360/export/start',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectIds:bimIds})});
    NP.export.total=ids.length;npUpdateExport();
  }catch(e){if(typeof showToast!=='undefined')showToast('Export start failed: '+e.message,'error');}
}
function npOnEnterExport(){
  if(NP._startingExport){
    NP._startingExport=false;
    NP.export={running:true,completed:0,total:NP.selectedIds.size,noDm:0,success:0,accessDenied:0};
    // Clear stale file data from previous run so results table shows fresh values
    NP.projects.forEach(function(p){ delete p.files; delete p.size; delete p.status; });
    var bf=document.getElementById('np-btn-finalize');if(bf)bf.disabled=true;
  }
  npUpdateExport();
}
function npUpdateExport(){
  var c=NP.export,pct=c.total>0?Math.min(100,Math.round(c.completed/c.total*100)):0;
  var rmax=Math.max(1,c.total-c.noDm-(c.accessDenied||0)),rpct=Math.min(100,Math.round(c.success/rmax*100));
  var fe=document.getElementById('np-fetched');if(fe)fe.textContent=c.completed+'/'+c.total;
  var ff=document.getElementById('np-fill-fetch');if(ff)ff.style.width=pct+'%';
  var bf=document.getElementById('np-badge-fetch');if(bf){bf.className='np-exp-badge '+(c.running?'np-processing':'np-done-badge');bf.textContent=c.running?'PROCESSING':'DONE';}
  _npSetNoDm(c.noDm);
  var ad=document.getElementById('np-access-denied');if(ad)ad.textContent=String(c.accessDenied||0);
  var re=document.getElementById('np-reports');if(re)re.textContent=c.success+'/'+rmax;
  var rf=document.getElementById('np-fill-rep');if(rf)rf.style.width=rpct+'%';
  var br=document.getElementById('np-badge-rep');if(br){br.className='np-exp-badge '+(c.running?'np-finalizing':'np-done-badge');br.textContent=c.running?'FINALIZING':'DONE';}
  npSetOverall(pct,c.running?(pct>=80?'SYNCING FINAL MANIFEST':'PROCESSING'):'COMPLETE');
}
function npSetOverall(pct,label){
  var of=document.getElementById('np-overall-fill');if(of)of.style.width=pct+'%';
  var ol=document.getElementById('np-overall-lbl');if(ol)ol.textContent=label;
  var op=document.getElementById('np-overall-pct');if(op)op.textContent=pct+'%';
  var active=label!=='COMPLETE';
  var bar=document.getElementById('np-overall-bar-wrap');if(bar)bar.classList.toggle('np-bar-active',active);
  var dot=document.getElementById('np-overall-dot');if(dot)dot.classList.toggle('np-dot-hidden',!active);
}
function _npFormatBytes(bytes) {
  if (!bytes || bytes <= 0) return '—';
  if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(1) + ' GB';
  if (bytes >= 1048576)    return (bytes / 1048576).toFixed(1)    + ' MB';
  if (bytes >= 1024)       return (bytes / 1024).toFixed(1)       + ' KB';
  return bytes + ' B';
}
function _npTotalSizeBytes() {
  var total = 0;
  NP.projects.forEach(function(p) {
    if (!NP.selectedIds.has(p.id)) return;
    var ps = A.projStatuses && A.projStatuses[p.id];
    var bytes = (ps && ps.sizeBytes) || p.sizeBytes || 0;
    total += bytes;
  });
  return total;
}
function npOnEnterResults(){
  var s=NP.export.success,el=document.getElementById('np-res-total');
  if(el)el.textContent=s+' project'+(s!==1?'s':'')+' exported';
  var se=document.getElementById('np-res-size-total');
  if(se)se.textContent=_npFormatBytes(_npTotalSizeBytes());
  npRenderResults();
}
function npRenderResults(){
  var tbody=document.getElementById('np-res-tbody');if(!tbody)return;
  var se2=document.getElementById('np-res-size-total');
  if(se2)se2.textContent=_npFormatBytes(_npTotalSizeBytes());
  var search=(document.getElementById('np-res-search')?document.getElementById('np-res-search').value:'').toLowerCase();
  var filter=document.getElementById('np-res-filter')?document.getElementById('np-res-filter').value:'all';
  var e2=function(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;');};
  var list=NP.projects.filter(function(p){
    if(!NP.selectedIds.has(p.id))return false;
    if(filter==='acc'&&p.platform!=='acc')return false;
    if(filter==='bim360'&&p.platform!=='bim360')return false;
    if(search&&p.name.toLowerCase().indexOf(search)<0)return false;return true;
  });
  if(!list.length){tbody.innerHTML='<tr><td colspan="5" style="text-align:center;padding:32px;color:#64748b">No results.</td></tr>';return;}
  tbody.innerHTML=list.map(function(p){
    var platBadge = p.platform==='bim360'
      ? '<span class="np-badge-bim360">BIM360</span>'
      : '<span class="np-badge-acc">ACC</span>';

    // A.projStatuses always has the freshest data from the current run.
    // p.files/p.size may be stale from a previous run — use as last resort only.
    var ps     = A.projStatuses && A.projStatuses[p.id];
    var files  = (ps && ps.files)  || p.files  || '—';
    var size   = (ps && ps.size)   || p.size   || '—';
    var status = (ps && ps.status) || p.status || '';

    var statusBadge = status === 'success'
      ? '<span style="background:#dcfce7;color:#16a34a;font-weight:700;font-size:11px;padding:2px 8px;border-radius:99px">Done</span>'
      : status === 'no_dm'
        ? '<span style="background:#fef9c3;color:#a16207;font-size:11px;padding:2px 8px;border-radius:99px">No DM</span>'
        : status === 'access_denied'
          ? '<span style="background:#fee2e2;color:#dc2626;font-size:11px;padding:2px 8px;border-radius:99px">Access Denied</span>'
          : status === 'failed'
            ? '<span style="background:#fee2e2;color:#dc2626;font-size:11px;padding:2px 8px;border-radius:99px">Failed</span>'
            : '<span style="background:#f1f5f9;color:#94a3b8;font-size:11px;padding:2px 8px;border-radius:99px">Pending</span>';

    return '<tr>'
      +'<td style="font-weight:500;color:#1e293b">'+e2(p.name)+'</td>'
      +'<td>'+platBadge+'</td>'
      +'<td>'+statusBadge+'</td>'
      +'<td style="font-family:monospace;font-size:13px;text-align:right;padding-right:16px">'+e2(files)+'</td>'
      +'<td style="font-family:monospace;font-size:13px;text-align:right;padding-right:16px">'+e2(size)+'</td>'
      +'</tr>';
  }).join('');
}
function npDownloadReport() {
  var statusLabel = { success:'Done', no_dm:'No DM', access_denied:'Access Denied', failed:'Failed' };
  var rows = [['Project Name','Platform','Status','File Count','File Size']];
  NP.projects.forEach(function(p) {
    if (!NP.selectedIds.has(p.id)) return;
    var ps     = A.projStatuses && A.projStatuses[p.id];
    var files  = (ps && ps.files)  || p.files  || '—';
    var size   = (ps && ps.size)   || p.size   || '—';
    var status = (ps && ps.status) || p.status || '';
    var plat   = p.platform === 'bim360' ? 'BIM360' : 'ACC';
    rows.push([p.name, plat, statusLabel[status] || status || 'Pending', files, size]);
  });
  var csv = rows.map(function(r) {
    return r.map(function(v) { return '"' + String(v).replace(/"/g, '""') + '"'; }).join(',');
  }).join('\r\n');
  var blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8;' });
  var url  = URL.createObjectURL(blob);
  var a    = document.createElement('a');
  a.href = url;
  a.download = 'export-report-' + new Date().toISOString().slice(0,10) + '.csv';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
async function npResetCheckpoint() {
  var platforms = NP.platform === 'acc' ? ['acc']
                : NP.platform === 'bim360' ? ['bim360']
                : ['acc', 'bim360'];

  var platLabel = platforms.join(' + ').toUpperCase();
  if (!confirm('Reset all export progress for ' + platLabel + '?\n\nAll completed projects will be marked as pending and re-exported on the next run.')) return;

  var btn = document.getElementById('np-btn-reset-cp');
  if (btn) { btn.disabled = true; btn.textContent = 'Resetting…'; }

  try {
    await Promise.all(platforms.map(function(p) {
      return fetch('/api/' + p + '/checkpoint', { method: 'DELETE' });
    }));
    // SSE 'checkpoint-reset' will fire and reload projects via npLoadProjects
    // But also reload directly here as a safety net
    setTimeout(npLoadProjects, 800);
    if (typeof showToast !== 'undefined') showToast('Checkpoint reset for ' + platLabel + '.', 'info');
  } catch(e) {
    if (typeof showToast !== 'undefined') showToast('Reset failed: ' + e.message, 'error');
  } finally {
    if (btn) {
      btn.disabled = false;
      btn.innerHTML = '<svg viewBox="0 0 20 20" fill="currentColor" width="14"><path fill-rule="evenodd" d="M4 2a1 1 0 011 1v2.101a7.002 7.002 0 0111.601 2.566 1 1 0 11-1.885.666A5.002 5.002 0 005.999 7H9a1 1 0 010 2H4a1 1 0 01-1-1V3a1 1 0 011-1zm.008 9.057a1 1 0 011.276.61A5.002 5.002 0 0014.001 13H11a1 1 0 110-2h5a1 1 0 011 1v5a1 1 0 11-2 0v-2.101a7.002 7.002 0 01-11.601-2.566 1 1 0 01.61-1.276z"/></svg> Reset Checkpoint';
    }
  }
}

function npReset(){
  NP.selectedIds.clear();NP.projects=[];
  NP.export={running:false,completed:0,total:0,noDm:0,success:0,accessDenied:0};
  npGoStep(1);npShowIdle();
}
