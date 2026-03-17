(function () {
    'use strict';

    var API_BASE   = '/api/jellytubbing';
    var PLUGIN_ID  = 'c3d4e5f6-a7b8-9012-cdef-012345678901';

    function apiHeaders() {
        return {
            'Content-Type': 'application/json',
            'X-Emby-Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
        };
    }

    function showToast(msg) {
        if (typeof require === 'function') {
            require(['toast'], function (toast) { toast(msg); });
            return;
        }
        var el = document.createElement('div');
        el.style.cssText = 'position:fixed;bottom:2em;left:50%;transform:translateX(-50%);' +
            'background:#333;color:#fff;padding:0.8em 1.5em;border-radius:6px;' +
            'z-index:9999;font-size:0.95em;pointer-events:none;';
        el.textContent = msg;
        document.body.appendChild(el);
        setTimeout(function () { el.parentNode && el.parentNode.removeChild(el); }, 3000);
    }

    // -----------------------------------------------------------------------
    // Config load / save
    // -----------------------------------------------------------------------
    function loadConfig() {
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            document.getElementById('InvidiousInstanceUrl').value = config.InvidiousInstanceUrl || '';
            document.getElementById('YtDlpBinaryPath').value      = config.YtDlpBinaryPath || '';
            document.getElementById('PreferredQuality').value     = config.PreferredQuality || '720p';
            document.getElementById('TrendingRegion').value       = config.TrendingRegion || 'DE';
        });
    }

    function saveConfig() {
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            config.InvidiousInstanceUrl = document.getElementById('InvidiousInstanceUrl').value.trim();
            config.YtDlpBinaryPath      = document.getElementById('YtDlpBinaryPath').value.trim();
            config.PreferredQuality     = document.getElementById('PreferredQuality').value;
            config.TrendingRegion       = document.getElementById('TrendingRegion').value.trim().toUpperCase() || 'DE';

            ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                showToast('Einstellungen gespeichert.');
                checkInvidious();
            });
        });
    }

    // -----------------------------------------------------------------------
    // Invidious status check
    // -----------------------------------------------------------------------
    function checkInvidious() {
        var bar = document.getElementById('jt-invidious-status');
        bar.innerHTML = '<span class="jt-info">&#8987; Prüfe Invidious-Verbindung…</span>';

        fetch(API_BASE + '/test-invidious', { headers: apiHeaders() })
            .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
            .then(function (data) {
                if (data.reachable) {
                    bar.innerHTML = '<span class="jt-ok">&#10003;</span> Invidious erreichbar: ' + (data.message || '');
                } else {
                    bar.innerHTML = '<span class="jt-err">&#10007;</span> ' + (data.message || 'Invidious nicht erreichbar');
                }
            })
            .catch(function () {
                bar.innerHTML = '<span class="jt-err">&#10007;</span> Verbindungsfehler';
            });
    }

    // -----------------------------------------------------------------------
    // Init
    // -----------------------------------------------------------------------
    document.getElementById('jt-save-btn').addEventListener('click', saveConfig);
    document.getElementById('jt-test-btn').addEventListener('click', checkInvidious);

    loadConfig();
    checkInvidious();

}());
