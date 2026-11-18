/* MailDog 站点脚本：只用原生 API，任何一步失败都静默退回静态表现 */
(function () {
  'use strict';

  /* ---------------- 主题：跟随系统 / 亮色 / 暗色 ---------------- */
  var THEME_KEY = 'maildog-theme';
  var root = document.documentElement;
  var darkQuery = window.matchMedia('(prefers-color-scheme: dark)');
  var themeToggle = document.querySelector('[data-theme-toggle]');

  function readMode() {
    try {
      var stored = localStorage.getItem(THEME_KEY);
      if (stored === 'light' || stored === 'dark') return stored;
    } catch (e) { /* 隐私模式下忽略 */ }
    return 'auto';
  }

  function applyMode(mode) {
    root.setAttribute('data-theme-mode', mode);
    if (mode === 'auto') {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', mode);
    }

    if (themeToggle) {
      var label = mode === 'auto'
        ? '主题跟随系统，点击切换为亮色'
        : mode === 'light'
          ? '当前为亮色主题，点击切换为暗色'
          : '当前为暗色主题，点击切换为跟随系统';
      themeToggle.setAttribute('aria-label', label);
      themeToggle.setAttribute('title', label);
    }
  }

  var mode = readMode();
  applyMode(mode);

  if (themeToggle) {
    themeToggle.addEventListener('click', function () {
      mode = mode === 'auto' ? 'light' : mode === 'light' ? 'dark' : 'auto';
      try {
        if (mode === 'auto') localStorage.removeItem(THEME_KEY);
        else localStorage.setItem(THEME_KEY, mode);
      } catch (e) { /* 忽略 */ }
      applyMode(mode);
    });
  }

  var onSystemChange = function () { if (mode === 'auto') applyMode('auto'); };
  if (typeof darkQuery.addEventListener === 'function') darkQuery.addEventListener('change', onSystemChange);
  else if (typeof darkQuery.addListener === 'function') darkQuery.addListener(onSystemChange);

  /* ---------------- 移动端菜单：点链接后收起 ---------------- */
  var menu = document.getElementById('menu');
  if (menu) {
    document.querySelectorAll('.nav-links a').forEach(function (link) {
      link.addEventListener('click', function () { menu.checked = false; });
    });
  }

  /* ---------------- 年份 ---------------- */
  document.querySelectorAll('[data-year]').forEach(function (node) {
    node.textContent = String(new Date().getFullYear());
  });

  /* ---------------- 最新 Release：版本号与下载地址 ---------------- */
  var REPO = 'refout/MailDog';
  var RELEASES = 'https://github.com/' + REPO + '/releases';
  var LATEST = RELEASES + '/latest';

  if (!document.querySelector('[data-dl], [data-release-version]')) return;

  function applyRelease(release) {
    if (!release) return;

    var assets = release.assets || [];
    var pick = function (suffix) {
      for (var i = 0; i < assets.length; i += 1) {
        if (assets[i].name.slice(-suffix.length) === suffix) return assets[i];
      }
      return null;
    };

    var setup = pick('-win-x64-setup.exe');
    var zipX64 = pick('-win-x64.zip');
    var zipArm = pick('-win-arm64.zip');
    var targets = { setup: setup, 'zip-x64': zipX64, 'zip-arm64': zipArm, primary: setup || zipX64 };

    document.querySelectorAll('[data-dl]').forEach(function (node) {
      var asset = targets[node.getAttribute('data-dl')];
      if (asset) node.href = asset.browser_download_url;
    });

    if (release.tag_name) {
      document.querySelectorAll('[data-release-version]').forEach(function (node) {
        node.textContent = release.tag_name;
      });
    }
    if (release.published_at) {
      var d = new Date(release.published_at);
      var iso = d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
      document.querySelectorAll('[data-release-date]').forEach(function (node) {
        node.textContent = iso;
      });
    }
  }

  fetch('https://api.github.com/repos/' + REPO + '/releases/latest', {
    headers: { Accept: 'application/vnd.github+json' }
  })
    .then(function (response) { return response.ok ? response.json() : null; })
    .then(applyRelease)
    .catch(function () { /* 离线或超限：保持指向 Releases 页面 */ });

  document.querySelectorAll('[data-dl]').forEach(function (node) {
    if (!node.getAttribute('href') || node.getAttribute('href') === '#') node.href = LATEST;
  });
})();
