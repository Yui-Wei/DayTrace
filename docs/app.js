// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Yui-Wei

(() => {
  'use strict';
  const repo = 'https://github.com/Yui-Wei/DayTrace';
  const shot = document.getElementById('product-shot');
  const imageLink = document.querySelector('[data-preview-link]');
  const buttons = document.querySelectorAll('[data-preview]');
  buttons.forEach(button => button.addEventListener('click', () => {
    const theme = button.dataset.preview;
    if (theme !== 'light' && theme !== 'dark') return;
    shot.src = `assets/overview-${theme}.png`;
    imageLink.href = shot.src;
    buttons.forEach(item => item.setAttribute('aria-pressed', String(item === button)));
  }));
  // Downloading never depends on this optional metadata request.
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 4500);
  fetch('https://api.github.com/repos/Yui-Wei/DayTrace/releases/latest', {
    headers: { Accept: 'application/vnd.github+json' }, signal: controller.signal
  }).then(response => {
    if (!response.ok) throw new Error('Release information unavailable');
    return response.json();
  }).then(release => {
    if (release.draft || release.prerelease || !/^v\d+\.\d+\.\d+$/.test(release.tag_name)) return;
    const asset = Array.isArray(release.assets) && release.assets.find(item => item.name === 'DayTrace.exe');
    let detail = `${release.tag_name} · Windows 11 · 64 位 · 免安装`;
    if (asset && Number.isFinite(asset.size) && asset.size > 0) {
      const size = asset.size < 1024 * 1024 ? `${Math.ceil(asset.size / 1024)} KB` : `${(asset.size / 1024 / 1024).toFixed(1)} MB`;
      detail = `${release.tag_name} · ${size} · 64 位 · 免安装`;
    }
    document.querySelectorAll('[data-release-meta]').forEach(node => { node.textContent = detail; });
  }).catch(() => {
    // Keep the latest-release download links and neutral labels usable offline or when GitHub rate-limits requests.
  }).finally(() => clearTimeout(timeout));
})();
