/**
 * Rig Control Plane & Visual Studio // Frontend Controller
 * Built on @rtkelly13/design-system Midnight Brutalist Tokens
 */

(function () {
  'use strict';

  // Application State
  const state = {
    activeTab: 'workbench', // 'estate' | 'workbench'
    projects: [],
    estateProjects: [],
    currentProject: null,
    currentReference: null,
    currentCandidate: null,
    currentMode: 'side-by-side',
    zoom: 2,
    metrics: {
      ssim: null,
      psnr: null,
      rmse: null,
      cand_size: null,
    },
  };

  // DOM Elements
  const dom = {
    // Top Tabs
    envBadge: document.getElementById('env-badge'),
    tabBtnEstate: document.getElementById('tab-btn-estate'),
    tabBtnWorkbench: document.getElementById('tab-btn-workbench'),
    viewEstate: document.getElementById('view-estate'),
    viewWorkbench: document.getElementById('view-workbench'),
    workbenchControlsGroup: document.getElementById('workbench-controls-group'),
    studioStatus: document.getElementById('studio-status'),

    // Estate View
    estateGrid: document.getElementById('estate-grid'),
    estateFilterBar: document.getElementById('estate-filter-bar'),
    estateRepoCount: document.getElementById('estate-repo-count'),
    btnRefreshEstate: document.getElementById('btn-refresh-estate'),
    btnBackupDb: document.getElementById('btn-backup-db'),

    // Workbench Elements
    projectSelect: document.getElementById('project-select'),
    modeSideBySide: document.getElementById('mode-side-by-side'),
    modeSplitSlider: document.getElementById('mode-split-slider'),
    modeDiffHeatmap: document.getElementById('mode-diff-heatmap'),
    zoomButtons: document.querySelectorAll('.zoom-btn'),
    btnQuickSnapshot: document.getElementById('btn-quick-snapshot'),
    referenceList: document.getElementById('reference-list'),
    cutsList: document.getElementById('cuts-list'),
    variationsTree: document.getElementById('variations-tree'),

    metricRefName: document.getElementById('metric-ref-name'),
    metricCandName: document.getElementById('metric-cand-name'),
    metricSsim: document.getElementById('metric-ssim'),
    metricPsnr: document.getElementById('metric-psnr'),
    metricRmse: document.getElementById('metric-rmse'),
    metricLum: document.getElementById('metric-lum'),
    metricChrom: document.getElementById('metric-chrom'),
    metricEdge: document.getElementById('metric-edge'),
    metricBias: document.getElementById('metric-bias'),
    metricSize: document.getElementById('metric-size'),

    modeDiagnostics: document.getElementById('mode-diagnostics'),
    diagnosticsDrawer: document.getElementById('diagnostics-drawer'),
    diagnosticsMode: document.getElementById('diagnostics-mode'),
    diagnosticsHotspotsList: document.getElementById('diagnostics-hotspots-list'),
    diagnosticsRecsList: document.getElementById('diagnostics-recs-list'),

    viewport: document.getElementById('workbench-viewport'),
    containerReference: document.getElementById('container-reference'),
    containerCandidate: document.getElementById('container-candidate'),
    containerHeatmap: document.getElementById('container-heatmap'),
    imgReference: document.getElementById('img-reference'),
    imgCandidate: document.getElementById('img-candidate'),
    imgHeatmap: document.getElementById('img-heatmap'),
    panelRefInfo: document.getElementById('panel-ref-info'),
    panelCandInfo: document.getElementById('panel-cand-info'),

    splitSliderContainer: document.getElementById('split-slider-container'),
    sliderDivider: document.getElementById('slider-divider'),
    sliderClipOverlay: document.getElementById('slider-clip-overlay'),
    sliderImgReference: document.getElementById('slider-img-reference'),
    sliderImgCandidate: document.getElementById('slider-img-candidate'),
    diffHeatmapContainer: document.getElementById('diff-heatmap-container'),
  };

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------
  function setStatus(msg, isError = false) {
    if (!dom.studioStatus) return;
    dom.studioStatus.textContent = msg;
    dom.studioStatus.style.color = isError
      ? 'var(--ds-intent-danger, #f955a4)'
      : 'var(--ds-intent-success, #39ff14)';
    setTimeout(() => {
      if (dom.studioStatus.textContent === msg) {
        dom.studioStatus.textContent = '';
      }
    }, 4000);
  }

  // -------------------------------------------------------------------------
  // Navigation Tab Switching
  // -------------------------------------------------------------------------
  function switchTab(tab) {
    const path = '/' + (tab === 'workbench' ? 'studio' : tab === 'estate' ? 'estate/operations' : tab);
    if (location.pathname !== path) { history.pushState(null, '', path); window.dispatchEvent(new PopStateEvent('popstate')); }
    state.activeTab = tab;
    [dom.tabBtnEstate, dom.tabBtnWorkbench].forEach((b) => b?.classList.remove('ds-btn-active'));
    [dom.viewEstate, dom.viewWorkbench].forEach((v) => v?.classList.remove('active'));

    if (tab === 'estate') {
      dom.tabBtnEstate?.classList.add('ds-btn-active');
      dom.viewEstate?.classList.add('active');
      if (dom.workbenchControlsGroup) dom.workbenchControlsGroup.style.display = 'none';
      fetchEstate();
    } else {
      dom.tabBtnWorkbench?.classList.add('ds-btn-active');
      dom.viewWorkbench?.classList.add('active');
      if (dom.workbenchControlsGroup) dom.workbenchControlsGroup.style.display = 'flex';
      applyZoom();
    }
  }

  window.addEventListener('rig:view', event => switchTab(event.detail));

  dom.tabBtnEstate?.addEventListener('click', () => switchTab('estate'));
  dom.tabBtnWorkbench?.addEventListener('click', () => switchTab('workbench'));
  dom.btnRefreshEstate?.addEventListener('click', () => fetchEstate());

  // -------------------------------------------------------------------------
  // Estate Repositories View
  // -------------------------------------------------------------------------
  async function fetchEstate() {
    try {
      const res = await fetch('/api/estate');
      if (!res.ok) throw new Error(`HTTP error ${res.status}`);
      const data = await res.json();
      state.estateProjects = data.projects || [];
      renderEstateGrid();
    } catch (err) {
      console.error('Failed to load estate repositories:', err);
      setStatus(`[ ERR: ${err.message} ]`, true);
    }
  }

  let activeEstateFilter = 'all';

  function getProjectGroupCategory(proj) {
    if (proj.enabled === false) return 'Archive';
    const group = (proj.orca_group || '').trim();
    if (!group) return 'Core Tooling';
    if (group.startsWith('rtkelly-labs')) return 'rtkelly-labs';
    if (group.startsWith('Resultful')) return 'Resultful';
    if (group.startsWith('personal/knowledge')) return 'personal / knowledge';
    if (group.startsWith('personal/apps')) return 'personal / apps';
    if (group.startsWith('personal/tooling')) return 'personal / tooling';
    return group;
  }

  function renderEstateGrid() {
    if (!dom.estateGrid) return;
    dom.estateGrid.innerHTML = '';
    const active = state.estateProjects.filter((p) => p.enabled !== false);
    if (dom.estateRepoCount) {
      dom.estateRepoCount.textContent = `${active.length} active repositories in personal virtual monorepo`;
    }

    // Build categories and populate filter bar
    const categoryCounts = new Map();
    categoryCounts.set('all', state.estateProjects.length);
    state.estateProjects.forEach((proj) => {
      const cat = getProjectGroupCategory(proj);
      categoryCounts.set(cat, (categoryCounts.get(cat) || 0) + 1);
    });

    if (dom.estateFilterBar) {
      dom.estateFilterBar.innerHTML = '';
      for (const [cat, count] of categoryCounts.entries()) {
        const btn = document.createElement('button');
        btn.className = `ds-btn estate-filter-btn ${activeEstateFilter === cat ? 'ds-btn-active' : ''}`;
        btn.textContent = `[ ${cat.toUpperCase()} (${count}) ]`;
        btn.addEventListener('click', () => {
          activeEstateFilter = cat;
          renderEstateGrid();
        });
        dom.estateFilterBar.appendChild(btn);
      }
    }

    // Filter projects
    const filteredProjects = activeEstateFilter === 'all'
      ? state.estateProjects
      : state.estateProjects.filter((p) => getProjectGroupCategory(p) === activeEstateFilter);

    // Group filtered projects by category
    const grouped = new Map();
    filteredProjects.forEach((proj) => {
      const cat = getProjectGroupCategory(proj);
      if (!grouped.has(cat)) grouped.set(cat, []);
      grouped.get(cat).push(proj);
    });

    // Render group sections
    for (const [catName, projects] of grouped.entries()) {
      const section = document.createElement('div');
      section.className = 'estate-group-section';

      const header = document.createElement('div');
      header.className = 'estate-group-header';
      header.innerHTML = `
        <div class="estate-group-title">
          <span style="color:var(--ds-accent-primary,#22d3ee);">//</span>
          <span>${catName}</span>
          <span style="font-size:11px;color:var(--ds-text-muted,#94a3b8);font-weight:400;">[ ${projects.length} ]</span>
        </div>
      `;
      section.appendChild(header);

      const grid = document.createElement('div');
      grid.className = 'estate-grid';

      projects.forEach((proj) => {
        const card = document.createElement('div');
        card.className = 'estate-card';
        if (proj.color) {
          card.style.borderLeft = `4px solid ${proj.color}`;
        }

        const gitHubUrl = proj.repo.replace('git@github.com:', 'https://github.com/').replace(/\.git$/, '');
        const repoSlug = gitHubUrl.replace('https://github.com/', '');
        const bannerHtml = proj.banner_url
          ? `<div class="estate-card-banner" style="background-image: url('${proj.banner_url}');"></div>`
          : '';
        const logoHtml = proj.logo_url
          ? `<img src="${proj.logo_url}" alt="${proj.name}" class="estate-card-logo" onerror="this.style.display='none'">`
          : `<div class="estate-card-logo" style="display:flex;align-items:center;justify-content:center;color:${proj.color || 'var(--ds-accent-primary,#22d3ee)'};font-weight:700;font-size:12px;">//</div>`;

        const iconHtml = proj.icon
          ? `<span style="border: 1px solid ${proj.color || 'var(--ds-border-default)'}; color: ${proj.color || 'var(--ds-text-muted)'}; font-size: 10px; padding: 1px 4px; font-family: monospace; border-radius: 2px; margin-left: 6px;">${proj.icon}</span>`
          : '';

        const badgeStyle = proj.color
          ? `background: ${proj.color}18; color: ${proj.color}; border-color: ${proj.color}55;`
          : '';

        card.innerHTML = `
          ${bannerHtml}
          <div class="estate-card-top">
            <div class="estate-card-title-group">
              ${logoHtml}
              <div>
                <div style="display:flex; align-items:center;">
                  <a href="${gitHubUrl}" target="_blank" rel="noopener" class="estate-card-name">${proj.name}</a>
                  ${iconHtml}
                </div>
                <div class="estate-card-domain">
                  <span style="display:inline-block;width:7px;height:7px;border-radius:50%;background-color:${proj.color || '#22d3ee'};margin-right:5px;vertical-align:middle;"></span>
                  [ ${proj.orca_group ? `${proj.orca_group} · ` : ''}${proj.domain || 'Infrastructure'} ]
                </div>
              </div>
            </div>
            <span class="estate-badge" style="${badgeStyle}">${proj.stack || 'General'}</span>
          </div>
          <div class="estate-card-desc">${proj.description || 'Estate repository.'}</div>
          <div class="estate-card-bottom">
            <span style="color: ${proj.enabled === false ? 'var(--ds-text-muted)' : 'var(--ds-intent-success, #39ff14)'};">
              ${proj.enabled === false ? '● Archive' : '● Managed'}
            </span>
            <div style="display:flex; gap:6px;">
              ${proj.enabled !== false ? `<a href="/repos/${repoSlug}/triage/inbox" class="ds-btn ds-btn-sm" style="text-decoration:none;">[ 📋 Queue ]</a>` : ''}
            </div>
          </div>
        `;

        grid.appendChild(card);
      });

      section.appendChild(grid);
      dom.estateGrid.appendChild(section);
    }
  }

  // -------------------------------------------------------------------------
  // Server Status & Database Backup (jobs live in the SPA at /ops/jobs)
  // -------------------------------------------------------------------------
  async function fetchStatus() {
    try {
      const res = await fetch('/api/status');
      if (!res.ok) return;
      const data = await res.json();
      if (dom.envBadge) {
        dom.envBadge.textContent = `[ ${data.environment} :${data.port} ]`;
        dom.envBadge.style.borderColor = data.is_preview ? '#f955a4' : '#22d3ee';
        dom.envBadge.style.color = data.is_preview ? '#f955a4' : '#22d3ee';
      }
    } catch {
      // Ignore background status failure
    }
  }

  dom.btnBackupDb?.addEventListener('click', async () => {
    setStatus('[ CREATING SQLITE DATABASE BACKUP... ]');
    try {
      const res = await fetch('/api/db/backup', { method: 'POST' });
      const data = await res.json();
      if (data.status === 'ok') {
        const fname = data.backup_file.split('/').pop();
        setStatus(`[ DB BACKUP SAVED: ${fname} ]`);
      } else {
        setStatus('[ BACKUP FAILED ]', true);
      }
    } catch (err) {
      setStatus(`[ ERR: ${err.message} ]`, true);
    }
  });

  // -------------------------------------------------------------------------
  // Workbench API Calls
  // -------------------------------------------------------------------------
  async function fetchProjects() {
    try {
      const res = await fetch('/api/projects');
      if (!res.ok) throw new Error(`HTTP error ${res.status}`);
      const data = await res.json();
      state.projects = data.projects || [];
      populateProjectSelect();
      if (state.projects.length > 0) {
        const urlParams = new URLSearchParams(window.location.search);
        const requestedProj = urlParams.get('project');
        const initialProj = state.projects.find((p) => p.name === requestedProj) || state.projects[0];
        selectProject(initialProj.name);
      }
    } catch (err) {
      console.error('Failed to load projects:', err);
      setStatus(`[ ERR: ${err.message} ]`, true);
    }
  }

  function populateProjectSelect() {
    dom.projectSelect.innerHTML = '';
    state.projects.forEach((proj) => {
      const opt = document.createElement('option');
      opt.value = proj.name;
      opt.textContent = `[ ${proj.name} ]`;
      dom.projectSelect.appendChild(opt);
    });
  }

  function selectProject(projectName) {
    const proj = state.projects.find((p) => p.name === projectName);
    if (!proj) return;

    state.currentProject = proj;
    dom.projectSelect.value = projectName;

    if (proj.references && proj.references.length > 0) {
      const primary = proj.references.find((r) => r.sequence_number === 1) || proj.references[0];
      state.currentReference = primary;
    } else {
      state.currentReference = null;
    }

    if (proj.master_svg) {
      state.currentCandidate = {
        filename: proj.master_svg.split('/').pop(),
        url: proj.master_svg,
        cut_type: 'master_svg',
      };
    } else if (proj.production_cuts && proj.production_cuts.length > 0) {
      state.currentCandidate = proj.production_cuts[0];
    } else {
      state.currentCandidate = null;
    }

    renderSidebar();
    if (dom.imgHeatmap) {
      dom.imgHeatmap.removeAttribute('src');
      dom.imgHeatmap.style.display = 'none';
    }
    updateViewports();
    computeDiff();
  }

  // -------------------------------------------------------------------------
  // Sidebar Rendering
  // -------------------------------------------------------------------------
  function renderSidebar() {
    const proj = state.currentProject;
    if (!proj) return;

    // 1. References List
    dom.referenceList.innerHTML = '';
    if (!proj.references || proj.references.length === 0) {
      dom.referenceList.innerHTML = '<li class="empty-hint">No reference sequence found</li>';
    } else {
      proj.references.forEach((ref) => {
        const li = document.createElement('li');
        li.className = 'asset-tree-item' + (state.currentReference && state.currentReference.filename === ref.filename ? ' selected' : '');
        li.innerHTML = `
          <span class="asset-name">${ref.filename}</span>
          <span class="asset-size">#${ref.sequence_number}</span>
        `;
        li.addEventListener('click', () => {
          state.currentReference = ref;
          renderSidebar();
          updateViewports();
          computeDiff();
        });
        dom.referenceList.appendChild(li);
      });
    }

    // 2. Production Cuts List
    dom.cutsList.innerHTML = '';
    if (!proj.production_cuts || proj.production_cuts.length === 0) {
      dom.cutsList.innerHTML = '<li class="empty-hint">No production cuts found</li>';
    } else {
      proj.production_cuts.forEach((cut) => {
        const li = document.createElement('li');
        li.className = 'asset-tree-item' + (state.currentCandidate && state.currentCandidate.filename === cut.filename ? ' selected' : '');
        li.innerHTML = `
          <span class="asset-name">${cut.filename}</span>
          <span class="asset-size">${cut.format.toUpperCase()}</span>
        `;
        li.addEventListener('click', () => {
          state.currentCandidate = cut;
          renderSidebar();
          updateViewports();
          computeDiff();
        });
        dom.cutsList.appendChild(li);
      });
    }

    // 3. Variations Runs & Snapshots
    dom.variationsTree.innerHTML = '';
    if (!proj.variation_runs || proj.variation_runs.length === 0) {
      dom.variationsTree.innerHTML = '<div class="empty-hint">No variations or snapshots</div>';
    } else {
      proj.variation_runs.forEach((run) => {
        const runBlock = document.createElement('div');
        runBlock.className = 'variation-run-block';
        runBlock.innerHTML = `<div class="run-header">[ RUN: ${run.run_id} ]</div>`;

        const stepList = document.createElement('ul');
        stepList.className = 'asset-tree-list';

        run.steps.forEach((step) => {
          const li = document.createElement('li');
          li.className = 'asset-tree-item' + (state.currentCandidate && state.currentCandidate.filename === (step.png_url || step.svg_url) ? ' selected' : '');
          li.innerHTML = `
            <span class="asset-name">#${step.step_idx} ${step.step_name}</span>
          `;
          li.addEventListener('click', () => {
            state.currentCandidate = {
              filename: `${run.run_id}_step_${step.step_idx}`,
              url: step.png_url || step.svg_url,
              cut_type: 'snapshot_step',
            };
            renderSidebar();
            updateViewports();
            computeDiff();
          });
          stepList.appendChild(li);
        });

        runBlock.appendChild(stepList);
        dom.variationsTree.appendChild(runBlock);
      });
    }
  }

  // -------------------------------------------------------------------------
  // Viewports & Diff Calculation
  // -------------------------------------------------------------------------
  function updateViewports() {
    const ref = state.currentReference;
    const cand = state.currentCandidate;

    dom.metricRefName.textContent = ref ? ref.filename : '-';
    dom.metricCandName.textContent = cand ? cand.filename : '-';

    if (ref) {
      dom.imgReference.src = ref.url;
      dom.imgReference.style.display = 'block';
      dom.sliderImgReference.src = ref.url;
      dom.sliderImgReference.style.display = 'block';
      dom.panelRefInfo.textContent = ref.description || ref.filename;
    } else {
      dom.imgReference.removeAttribute('src');
      dom.imgReference.style.display = 'none';
      dom.sliderImgReference.removeAttribute('src');
      dom.sliderImgReference.style.display = 'none';
      dom.panelRefInfo.textContent = 'None';
    }

    if (cand) {
      dom.imgCandidate.src = cand.url;
      dom.imgCandidate.style.display = 'block';
      dom.sliderImgCandidate.src = cand.url;
      dom.sliderImgCandidate.style.display = 'block';
      dom.panelCandInfo.textContent = cand.purpose || cand.filename;
    } else {
      dom.imgCandidate.removeAttribute('src');
      dom.imgCandidate.style.display = 'none';
      dom.sliderImgCandidate.removeAttribute('src');
      dom.sliderImgCandidate.style.display = 'none';
      dom.panelCandInfo.textContent = 'None';
    }
  }

  async function computeDiff() {
    const ref = state.currentReference;
    const cand = state.currentCandidate;
    if (!ref || !cand) return;

    setStatus('[ CALCULATING DRIFT... ]');
    try {
      const url = `/api/diff?ref=${encodeURIComponent(ref.url)}&cand=${encodeURIComponent(cand.url)}`;
      const res = await fetch(url);
      if (!res.ok) throw new Error(`HTTP error ${res.status}`);
      const data = await res.json();

      state.metrics = data;
      dom.metricSsim.textContent = (data.ssim * 100).toFixed(2) + '%';
      dom.metricPsnr.textContent = data.psnr_db.toFixed(1) + ' dB';
      dom.metricRmse.textContent = data.rmse_pct.toFixed(2) + '%';
      if (data.diff_heatmap_data) {
        dom.imgHeatmap.src = data.diff_heatmap_data;
        dom.imgHeatmap.style.display = 'block';
      } else {
        dom.imgHeatmap.removeAttribute('src');
        dom.imgHeatmap.style.display = 'none';
      }

      if (data.decomposed) {
        dom.metricLum.textContent = data.decomposed.luminance_rmse_pct.toFixed(1) + '%';
        dom.metricChrom.textContent = data.decomposed.chrominance_rmse_pct.toFixed(1) + '%';
        dom.metricEdge.textContent = data.decomposed.edge_correlation.toFixed(1);
        dom.metricBias.textContent = data.decomposed.boundary_dilation_bias.toFixed(3);
      }
      setStatus('[ METRICS_SYNCHRONIZED ]');
    } catch (err) {
      console.error('Diff error:', err);
      setStatus(`[ DRIFT_ERR: ${err.message} ]`, true);
    }
  }

  function applyZoom() {
    const stages = document.querySelectorAll('.pan-zoom-stage');
    stages.forEach((stage) => {
      stage.style.transform = `scale(${state.zoom})`;
    });
  }

  // View Mode Toggles
  dom.modeSideBySide?.addEventListener('click', () => setWorkbenchMode('side-by-side'));
  dom.modeSplitSlider?.addEventListener('click', () => setWorkbenchMode('split-slider'));
  dom.modeDiffHeatmap?.addEventListener('click', () => setWorkbenchMode('diff-heatmap'));
  dom.modeDiagnostics?.addEventListener('click', () => {
    if (dom.diagnosticsDrawer) {
      dom.diagnosticsDrawer.style.display = dom.diagnosticsDrawer.style.display === 'none' ? 'block' : 'none';
    }
  });

  function setWorkbenchMode(mode) {
    state.currentMode = mode;
    [dom.modeSideBySide, dom.modeSplitSlider, dom.modeDiffHeatmap].forEach((btn) => btn?.classList.remove('ds-btn-active'));

    const sidePanel = document.getElementById('view-side-by-side');
    const sliderPanel = document.getElementById('view-split-slider');
    const heatmapPanel = document.getElementById('view-diff-heatmap');

    if (sidePanel) sidePanel.style.display = 'none';
    if (sliderPanel) sliderPanel.style.display = 'none';
    if (heatmapPanel) heatmapPanel.style.display = 'none';

    if (mode === 'side-by-side') {
      dom.modeSideBySide?.classList.add('ds-btn-active');
      if (sidePanel) sidePanel.style.display = 'flex';
    } else if (mode === 'split-slider') {
      dom.modeSplitSlider?.classList.add('ds-btn-active');
      if (sliderPanel) sliderPanel.style.display = 'block';
    } else if (mode === 'diff-heatmap') {
      dom.modeDiffHeatmap?.classList.add('ds-btn-active');
      if (heatmapPanel) heatmapPanel.style.display = 'block';
    }
  }

  // Split Slider Drag Interaction
  let isDraggingSlider = false;

  function updateSliderPosition(clientX) {
    if (!dom.splitSliderContainer) return;
    const rect = dom.splitSliderContainer.getBoundingClientRect();
    const x = clientX - rect.left;
    const pct = Math.max(0, Math.min(100, (x / rect.width) * 100));

    if (dom.sliderDivider) dom.sliderDivider.style.left = `${pct}%`;
    if (dom.sliderClipOverlay) dom.sliderClipOverlay.style.clipPath = `inset(0 ${100 - pct}% 0 0)`;
  }

  dom.splitSliderContainer?.addEventListener('mousedown', (e) => {
    isDraggingSlider = true;
    updateSliderPosition(e.clientX);
  });

  window.addEventListener('mousemove', (e) => {
    if (!isDraggingSlider) return;
    updateSliderPosition(e.clientX);
  });

  window.addEventListener('mouseup', () => {
    isDraggingSlider = false;
  });

  dom.splitSliderContainer?.addEventListener('touchstart', (e) => {
    if (e.touches.length > 0) {
      isDraggingSlider = true;
      updateSliderPosition(e.touches[0].clientX);
    }
  });

  window.addEventListener('touchmove', (e) => {
    if (isDraggingSlider && e.touches.length > 0) {
      updateSliderPosition(e.touches[0].clientX);
    }
  });

  window.addEventListener('touchend', () => {
    isDraggingSlider = false;
  });

  // Zoom Buttons
  dom.zoomButtons.forEach((btn) => {
    btn.addEventListener('click', () => {
      dom.zoomButtons.forEach((b) => b.classList.remove('ds-btn-active'));
      btn.classList.add('ds-btn-active');
      state.zoom = parseFloat(btn.dataset.zoom) || 1;
      applyZoom();
    });
  });

  dom.projectSelect?.addEventListener('change', (e) => {
    selectProject(e.target.value);
  });

  dom.btnQuickSnapshot?.addEventListener('click', async () => {
    if (!state.currentProject) return;
    const stepName = window.prompt(
      `Capture snapshot step for [${state.currentProject.name}]:`,
      'manual-checkpoint'
    );
    if (!stepName) return;

    const payload = {
      project: state.currentProject.name,
      step_name: stepName,
      svg_content: null,
      notes: `Captured via Asset Studio UI against ${state.currentReference ? state.currentReference.filename : 'baseline'}`,
      metrics: state.metrics,
    };

    try {
      const res = await fetch('/api/snapshot', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      setStatus(`[ SNAPSHOT CAPTURED: ${stepName} ]`);
      fetchProjects();
    } catch (err) {
      console.error('Failed to capture snapshot:', err);
      setStatus(`[ ERR: ${err.message} ]`, true);
    }
  });

  // -------------------------------------------------------------------------
  // Live Reload Polling (5s interval, pauses when tab is backgrounded)
  // -------------------------------------------------------------------------
  let lastMtime = null;
  let lastCssMtime = null;

  function reloadStylesheets() {
    const links = document.querySelectorAll('link[rel="stylesheet"]');
    links.forEach((link) => {
      const url = new URL(link.href, window.location.origin);
      url.searchParams.set('v', String(Date.now()));
      link.href = url.toString();
    });
    console.log('[LIVE_RELOAD] Hot-swapped CSS stylesheets without page reload');
  }

  async function checkLiveReload() {
    if (document.visibilityState !== 'visible') return;
    try {
      const res = await fetch('/api/dev/mtime');
      if (!res.ok) return;
      const data = await res.json();
      if (lastMtime === null) {
        lastMtime = data.mtime;
        lastCssMtime = data.css_mtime;
      } else if (data.mtime > lastMtime) {
        const isCssOnly = data.css_mtime > (lastCssMtime || 0) && data.mtime === data.css_mtime;
        lastMtime = data.mtime;
        lastCssMtime = data.css_mtime;
        if (isCssOnly) {
          reloadStylesheets();
        } else {
          console.log('[LIVE_RELOAD] Templates/scripts modified on disk; reloading page...');
          window.location.reload();
        }
      }
    } catch {
      // Server temporarily unreachable; ignore
    }
  }

  setInterval(checkLiveReload, 5000);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      checkLiveReload();
    }
  });

  // Initialization
  function init() {
    fetchStatus();
    fetchEstate();
    fetchProjects();
    checkLiveReload();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
