/* MailDog 站点脚本：只用原生 API，任何一步失败都静默退回静态链接 */
(function () {
  'use strict';

  var REPO = 'refout/MailDog';
  var RELEASES = 'https://github.com/' + REPO + '/releases';
  var LATEST = RELEASES + '/latest';

  /* 移动端菜单：点链接后收起 */
  var menu = document.getElementById('menu');
  if (menu) {
    document.querySelectorAll('.nav-links a').forEach(function (link) {
      link.addEventListener('click', function () { menu.checked = false; });
    });
  }

  /* 年份 */
  document.querySelectorAll('[data-year]').forEach(function (node) {
    node.textContent = String(new Date().getFullYear());
  });

  /* 从最新 Release 取版本号与安装包地址 */
  function apply(release) {
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
    var primary = setup || zipX64;

    var targets = { setup: setup, 'zip-x64': zipX64, 'zip-arm64': zipArm, primary: primary };

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
      document.querySelectorAll('[data-release-date]').forEach(function (node) {
        node.textContent = d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
      });
    }
  }

  if (!document.querySelector('[data-dl], [data-release-version]')) return;

  fetch('https://api.github.com/repos/' + REPO + '/releases/latest', {
    headers: { Accept: 'application/vnd.github+json' }
  })
    .then(function (response) { return response.ok ? response.json() : null; })
    .then(apply)
    .catch(function () { /* 离线或超限：保持指向 Releases 页面 */ });

  /* 首屏兜底：未取到版本时仍指向最新 Release */
  document.querySelectorAll('[data-dl]').forEach(function (node) {
    if (!node.getAttribute('href') || node.getAttribute('href') === '#') node.href = LATEST;
  });
})();
