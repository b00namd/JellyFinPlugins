(function () {
    'use strict';

    var API_BASE  = '/api/jellytubbing';
    var PLUGIN_ID = 'c3d4e5f6-a7b8-9012-cdef-012345678901';

    function apiHeaders() {
        return {
            'Content-Type': 'application/json',
            'X-Emby-Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
        };
    }

    function showToast(msg) {
        if (typeof require === 'function') {
            require(['toast'], function (t) { t(msg); });
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
            document.getElementById('YouTubeApiKey').value      = config.YouTubeApiKey || '';
            document.getElementById('OAuthClientId').value      = config.OAuthClientId || '';
            document.getElementById('OAuthClientSecret').value  = config.OAuthClientSecret || '';
            document.getElementById('JellyfinServerUrl').value  = config.JellyfinServerUrl || 'http://localhost:8096';
            document.getElementById('StrmOutputPath').value     = config.StrmOutputPath || '';
            document.getElementById('MaxVideosPerChannel').value = config.MaxVideosPerChannel || 25;
            document.getElementById('YtDlpBinaryPath').value    = config.YtDlpBinaryPath || '';
            document.getElementById('PreferredQuality').value   = config.PreferredQuality || '720p';
            document.getElementById('TrendingRegion').value     = config.TrendingRegion || 'DE';

            // Show OAuth redirect hint
            // Store synced channel IDs for checkbox rendering
            window._jtSyncedIds = config.SyncedChannelIds || [];
        });
    }

    function saveConfig() {
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            config.YouTubeApiKey       = document.getElementById('YouTubeApiKey').value.trim();
            config.OAuthClientId       = document.getElementById('OAuthClientId').value.trim();
            config.OAuthClientSecret   = document.getElementById('OAuthClientSecret').value.trim();
            config.StrmOutputPath      = document.getElementById('StrmOutputPath').value.trim();
            config.MaxVideosPerChannel = parseInt(document.getElementById('MaxVideosPerChannel').value, 10) || 25;
            config.YtDlpBinaryPath     = document.getElementById('YtDlpBinaryPath').value.trim();
            config.PreferredQuality    = document.getElementById('PreferredQuality').value;
            config.TrendingRegion      = document.getElementById('TrendingRegion').value;

            // Collect checked subscriptions
            var checked = document.querySelectorAll('.jt-sub-checkbox:checked');
            config.SyncedChannelIds = Array.from(checked).map(function (cb) { return cb.dataset.channelId; });

            // Update redirect hint
            var serverUrl = config.JellyfinServerUrl.replace(/\/+$/, '');
            var hint = document.getElementById('jt-redirect-hint');
            if (hint) hint.textContent = serverUrl + '/api/jellytubbing/oauth-callback';

            ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                showToast('Einstellungen gespeichert.');
                checkYtDlp();
            });
        });
    }

    // -----------------------------------------------------------------------
    // yt-dlp check
    // -----------------------------------------------------------------------

    function checkYtDlp() {
        var el = document.getElementById('jt-ytdlp-status');
        el.innerHTML = '<span class="jt-info">&#8987; yt-dlp wird geprueft...</span>';

        fetch(API_BASE + '/check-tools', { headers: apiHeaders() })
            .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
            .then(function (data) {
                if (data.ytDlpAvailable) {
                    el.innerHTML = '<span class="jt-ok">&#10003;</span> yt-dlp ' + (data.ytDlpVersion || '');
                } else {
                    el.innerHTML = '<span class="jt-err">&#10007;</span> yt-dlp nicht gefunden' +
                        (data.ytDlpError ? ' (' + data.ytDlpError + ')' : '');
                }
            })
            .catch(function () {
                el.innerHTML = '<span class="jt-err">&#10007;</span> Fehler';
            });
    }

    // -----------------------------------------------------------------------
    // OAuth status
    // -----------------------------------------------------------------------

    function checkOAuthStatus() {
        var el = document.getElementById('jt-oauth-status');
        el.innerHTML = '<span class="jt-info">&#8987; Google-Status wird geprueft...</span>';

        fetch(API_BASE + '/oauth-status', { headers: apiHeaders() })
            .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
            .then(function (data) {
                var oauthBtn  = document.getElementById('jt-oauth-btn');
                var revokeBtn = document.getElementById('jt-oauth-revoke-btn');
                var loadBtn   = document.getElementById('jt-load-subs-btn');
                var syncBtn   = document.getElementById('jt-sync-btn');

                if (data.authorized) {
                    el.innerHTML = '<span class="jt-ok">&#10003;</span> Mit Google verbunden';
                    if (oauthBtn)  oauthBtn.style.display  = 'none';
                    if (revokeBtn) revokeBtn.style.display  = '';
                    if (loadBtn)   loadBtn.style.display    = '';
                    if (syncBtn)   syncBtn.style.display    = '';
                    loadSubscriptions();
                } else {
                    el.innerHTML = '<span class="jt-info">Nicht mit Google verbunden</span>';
                    if (oauthBtn)  oauthBtn.style.display  = '';
                    if (revokeBtn) revokeBtn.style.display  = 'none';
                    if (loadBtn)   loadBtn.style.display    = 'none';
                    if (syncBtn)   syncBtn.style.display    = 'none';
                }
            })
            .catch(function () {
                var el2 = document.getElementById('jt-oauth-status');
                if (el2) el2.innerHTML = '<span class="jt-err">&#10007;</span> Statusfehler';
            });
    }

    // -----------------------------------------------------------------------
    // OAuth – Device Authorization Grant
    // -----------------------------------------------------------------------

    var _pollTimer = null;

    function startOAuth() {
        // Save credentials first
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            config.OAuthClientId     = document.getElementById('OAuthClientId').value.trim();
            config.OAuthClientSecret = document.getElementById('OAuthClientSecret').value.trim();
            return ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
        }).then(function () {
            return fetch(API_BASE + '/oauth-device-start', { method: 'POST', headers: apiHeaders() });
        }).then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
        .then(function (data) {
            if (!data.success) { showToast(data.message || 'Fehler'); return; }
            showDeviceBox(data);
            startPolling(data.deviceCode, Math.max(data.interval || 5, 5) * 1000);
        })
        .catch(function () { showToast('Fehler beim Starten der Autorisierung.'); });
    }

    function showDeviceBox(data) {
        var box = document.getElementById('jt-device-box');
        document.getElementById('jt-device-url').textContent  = data.verificationUrl;
        document.getElementById('jt-device-code').textContent = data.userCode;
        document.getElementById('jt-device-hint').innerHTML   = '&#8987; Warte auf Bestaetigung...';
        if (box) box.style.display = '';
    }

    function hideDeviceBox() {
        var box = document.getElementById('jt-device-box');
        if (box) box.style.display = 'none';
    }

    function startPolling(deviceCode, intervalMs) {
        if (_pollTimer) clearInterval(_pollTimer);
        _pollTimer = setInterval(function () {
            fetch(API_BASE + '/oauth-device-poll', {
                method: 'POST',
                headers: apiHeaders(),
                body: JSON.stringify({ deviceCode: deviceCode })
            })
            .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
            .then(function (data) {
                if (data.status === 'success') {
                    clearInterval(_pollTimer);
                    hideDeviceBox();
                    showToast('Google-Konto erfolgreich verbunden!');
                    checkOAuthStatus();
                } else if (data.status === 'denied') {
                    clearInterval(_pollTimer);
                    hideDeviceBox();
                    showToast('Zugriff verweigert.');
                } else if (data.status === 'expired') {
                    clearInterval(_pollTimer);
                    document.getElementById('jt-device-hint').innerHTML = '<span class="jt-err">Code abgelaufen. Bitte erneut versuchen.</span>';
                } else if (data.status === 'slow_down') {
                    clearInterval(_pollTimer);
                    startPolling(deviceCode, intervalMs + 5000);
                }
                // 'pending' → keep polling
            })
            .catch(function () { /* ignore poll errors */ });
        }, intervalMs);
    }

    function revokeOAuth() {
        if (_pollTimer) { clearInterval(_pollTimer); hideDeviceBox(); }
        fetch(API_BASE + '/oauth-revoke', { method: 'POST', headers: apiHeaders() })
            .then(function () {
                showToast('Google-Verbindung getrennt.');
                checkOAuthStatus();
                document.getElementById('jt-sub-list').style.display = 'none';
                var hint = document.getElementById('jt-subs-hint');
                if (hint) { hint.style.display = ''; hint.textContent = 'Verbinde dein Google-Konto, um Abonnements zu laden.'; }
            });
    }

    // -----------------------------------------------------------------------
    // Subscriptions
    // -----------------------------------------------------------------------

    function loadSubscriptions() {
        var hint    = document.getElementById('jt-subs-hint');
        var listEl  = document.getElementById('jt-sub-list');
        if (hint)   { hint.textContent = 'Abonnements werden geladen...'; hint.style.display = ''; }
        if (listEl) listEl.style.display = 'none';

        fetch(API_BASE + '/subscriptions', { headers: apiHeaders() })
            .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
            .then(function (data) {
                if (!data.success) {
                    if (hint) hint.textContent = data.message || 'Fehler beim Laden.';
                    return;
                }
                if (hint) hint.style.display = 'none';
                renderSubscriptions(data.subscriptions || [], listEl);
            })
            .catch(function () {
                if (hint) hint.textContent = 'Fehler beim Laden der Abonnements.';
            });
    }

    function renderSubscriptions(subs, listEl) {
        if (!listEl) return;
        listEl.innerHTML = '';

        // Checked channels first, then alphabetical within each group
        subs.sort(function (a, b) {
            if (a.synced !== b.synced) return a.synced ? -1 : 1;
            return a.title.localeCompare(b.title);
        });

        subs.forEach(function (sub) {
            var item = document.createElement('label');
            item.className = 'jt-sub-item';

            var cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.className = 'jt-sub-checkbox';
            cb.dataset.channelId = sub.channelId;
            cb.checked = !!sub.synced;
            cb.style.flexShrink = '0';

            var img = document.createElement('img');
            img.src = sub.thumbnail || '';
            img.alt = '';
            img.onerror = function () { this.style.display = 'none'; };

            var name = document.createElement('span');
            name.textContent = sub.title;

            item.appendChild(cb);
            item.appendChild(img);
            item.appendChild(name);
            listEl.appendChild(item);
        });

        listEl.style.display = subs.length ? '' : 'none';
        if (!subs.length) {
            var hint = document.getElementById('jt-subs-hint');
            if (hint) { hint.style.display = ''; hint.textContent = 'Keine Abonnements gefunden.'; }
        }
    }

    // -----------------------------------------------------------------------
    // Sync
    // -----------------------------------------------------------------------

    function triggerSync() {
        // Save config first, then start sync
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            config.YouTubeApiKey       = document.getElementById('YouTubeApiKey').value.trim();
            config.OAuthClientId       = document.getElementById('OAuthClientId').value.trim();
            config.OAuthClientSecret   = document.getElementById('OAuthClientSecret').value.trim();
            config.StrmOutputPath      = document.getElementById('StrmOutputPath').value.trim();
            config.MaxVideosPerChannel = parseInt(document.getElementById('MaxVideosPerChannel').value, 10) || 25;
            config.YtDlpBinaryPath     = document.getElementById('YtDlpBinaryPath').value.trim();
            config.PreferredQuality    = document.getElementById('PreferredQuality').value;
            config.TrendingRegion      = document.getElementById('TrendingRegion').value;

            var checked = document.querySelectorAll('.jt-sub-checkbox:checked');
            config.SyncedChannelIds = Array.from(checked).map(function (cb) { return cb.dataset.channelId; });

            return ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
        }).then(function () {
            return fetch(API_BASE + '/sync', { method: 'POST', headers: apiHeaders() });
        }).then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
        .then(function (data) { showToast(data.message || 'Sync gestartet.'); })
        .catch(function () { showToast('Fehler beim Starten der Synchronisation.'); });
    }

    // -----------------------------------------------------------------------
    // Init
    // -----------------------------------------------------------------------

    // JSON import
    document.getElementById('jt-json-import-btn').addEventListener('click', function () {
        document.getElementById('jt-json-file').click();
    });
    document.getElementById('jt-json-file').addEventListener('change', function (e) {
        var file = e.target.files && e.target.files[0];
        if (!file) return;
        var reader = new FileReader();
        reader.onload = function (ev) {
            var status = document.getElementById('jt-json-status');
            try {
                var json = JSON.parse(ev.target.result);
                var creds = json.web || json.installed;
                if (!creds || !creds.client_id || !creds.client_secret) {
                    status.textContent = 'Keine gueltigen Credentials gefunden.';
                    status.className = 'jt-err';
                    return;
                }
                document.getElementById('OAuthClientId').value     = creds.client_id;
                document.getElementById('OAuthClientSecret').value = creds.client_secret;
                status.textContent = 'Importiert: ' + file.name;
                status.className = 'jt-ok';
            } catch (err) {
                status.textContent = 'Fehler beim Lesen der JSON-Datei.';
                status.className = 'jt-err';
            }
        };
        reader.readAsText(file);
        // Reset so same file can be selected again
        e.target.value = '';
    });

    // -----------------------------------------------------------------------
    // Directory browser (uses Jellyfin /Environment API)
    // -----------------------------------------------------------------------

    var _dirCurrentPath = '/';

    function openDirBrowser() {
        var overlay = document.getElementById('jt-dir-overlay');
        if (!overlay) return;
        overlay.style.display = 'flex';
        var start = document.getElementById('StrmOutputPath').value.trim() || '/';
        loadDirContents(start);
    }

    function closeDirBrowser() {
        var overlay = document.getElementById('jt-dir-overlay');
        if (overlay) overlay.style.display = 'none';
    }

    function loadDirContents(path) {
        _dirCurrentPath = path;
        document.getElementById('jt-dir-path').textContent = path;
        var list = document.getElementById('jt-dir-list');
        list.innerHTML = '<div class="jt-dir-item" style="color:#888;">Wird geladen\u2026</div>';

        var url;
        if (!path || path === '/') {
            url = window.location.origin + '/Environment/Drives';
        } else {
            url = window.location.origin + '/Environment/DirectoryContents?path=' +
                encodeURIComponent(path) + '&includeFiles=false&includeDirectories=true';
        }

        fetch(url, { headers: apiHeaders() })
            .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status + ' ' + r.statusText); })
            .then(function (data) {
                list.innerHTML = '';
                var items = Array.isArray(data) ? data : (data.Items || []);

                if (path && path !== '/') {
                    var idx = path.replace(/[/\\]+$/, '').lastIndexOf('/');
                    var idxBs = path.replace(/[/\\]+$/, '').lastIndexOf('\\');
                    idx = Math.max(idx, idxBs);
                    var parent = idx > 0 ? path.substring(0, idx) : '/';
                    var back = document.createElement('div');
                    back.className = 'jt-dir-item jt-dir-up';
                    back.textContent = '\u2b06 ..';
                    back.addEventListener('click', function () { loadDirContents(parent); });
                    list.appendChild(back);
                }

                if (!items.length && path !== '/') {
                    var empty = document.createElement('div');
                    empty.className = 'jt-dir-item';
                    empty.style.color = '#888';
                    empty.textContent = '(Leer)';
                    list.appendChild(empty);
                    return;
                }

                items.forEach(function (item) {
                    var itemPath = item.Path || item.Name;
                    var el = document.createElement('div');
                    el.className = 'jt-dir-item';
                    el.textContent = '\uD83D\uDCC1 ' + (item.Name || itemPath);
                    el.addEventListener('click', function () { loadDirContents(itemPath); });
                    list.appendChild(el);
                });
            })
            .catch(function (err) {
                list.innerHTML = '<div class="jt-dir-item" style="color:#f44336;">Fehler: ' + String(err) + '</div>';
            });
    }

    document.getElementById('jt-browse-btn').addEventListener('click', openDirBrowser);
    document.getElementById('jt-dir-cancel').addEventListener('click', closeDirBrowser);
    document.getElementById('jt-dir-overlay').addEventListener('click', function (e) {
        if (e.target === this) closeDirBrowser();
    });
    document.getElementById('jt-dir-select').addEventListener('click', function () {
        if (_dirCurrentPath && _dirCurrentPath !== '/') {
            document.getElementById('StrmOutputPath').value = _dirCurrentPath;
        }
        closeDirBrowser();
    });

    document.getElementById('jt-save-btn').addEventListener('click', saveConfig);
    document.getElementById('jt-oauth-btn').addEventListener('click', startOAuth);
    document.getElementById('jt-oauth-revoke-btn').addEventListener('click', revokeOAuth);
    document.getElementById('jt-load-subs-btn').addEventListener('click', loadSubscriptions);
    document.getElementById('jt-sync-btn').addEventListener('click', triggerSync);

    loadConfig();
    checkYtDlp();
    checkOAuthStatus();

}());
