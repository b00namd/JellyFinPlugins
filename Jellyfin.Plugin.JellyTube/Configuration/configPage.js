(function () {
    'use strict';

    console.log('[YT] IIFE start');

    var API_BASE = '/api/jellytube';
    var PLUGIN_ID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
    var pollTimer = null;

    // -----------------------------------------------------------------------
    // Utilities
    // -----------------------------------------------------------------------
    function apiHeaders() {
        return {
            'Content-Type': 'application/json',
            'X-Emby-Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
        };
    }

    function fmtDate(iso) {
        if (!iso) return '';
        return new Date(iso).toLocaleString();
    }

    function fmtDuration(secs) {
        if (!secs) return '';
        var h = Math.floor(secs / 3600);
        var m = Math.floor((secs % 3600) / 60);
        var s = Math.floor(secs % 60);
        return (h ? h + 'h ' : '') + (m ? m + 'm ' : '') + s + 's';
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

    function escHtml(str) {
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    // -----------------------------------------------------------------------
    // Scheduled entries list
    // -----------------------------------------------------------------------
    function renderScheduledEntries(entries) {
        var container = document.getElementById('yt-scheduled-entries');
        container.innerHTML = '';
        (entries || []).forEach(function (entry) {
            addEntryRow(entry.Url || '', entry.DownloadPath || '', entry.MaxAgeDays || 0, !!entry.DeleteWatched);
        });
    }

    function addEntryRow(url, path, maxAgeDays, deleteWatched) {
        var container = document.getElementById('yt-scheduled-entries');
        var row = document.createElement('div');
        row.className = 'yt-entry-row';
        row.innerHTML =
            '<div class="yt-entry-row-main">' +
                '<input type="text" class="yt-entry-url emby-input" placeholder="https://..." value="' + escHtml(url) + '" style="flex:2;background:#1c1c1c;color:#fff;border:1px solid #444;border-radius:4px;padding:0.4em 0.6em;" />' +
                '<input type="text" class="yt-entry-path emby-input" placeholder="Verzeichnis (leer = Basis)" value="' + escHtml(path) + '" style="flex:2;background:#1c1c1c;color:#fff;border:1px solid #444;border-radius:4px;padding:0.4em 0.6em;" />' +
                '<button type="button" class="yt-entry-browse raised emby-button" style="padding:0.4em 0.8em;">&#128193;</button>' +
                '<button type="button" class="yt-entry-remove raised emby-button" style="padding:0.4em 0.8em;background:#5a1a1a;">&#10005;</button>' +
            '</div>' +
            '<div class="yt-entry-row-opts">' +
                '<label class="yt-entry-maxage-label">Max. Videoalter (Tage, 0=unbegrenzt): <input type="number" class="yt-entry-maxage" min="0" value="' + (maxAgeDays || 0) + '" /></label>' +
                '<label class="yt-entry-delwatched-label"><input type="checkbox" class="yt-entry-delwatched"' + (deleteWatched ? ' checked' : '') + ' /> Gesehene Videos löschen</label>' +
            '</div>';

        var pathInput = row.querySelector('.yt-entry-path');
        row.querySelector('.yt-entry-browse').addEventListener('click', function () {
            openDirBrowser(pathInput);
        });
        row.querySelector('.yt-entry-remove').addEventListener('click', function () {
            container.removeChild(row);
        });

        container.appendChild(row);
    }

    function collectScheduledEntries() {
        var rows = document.querySelectorAll('#yt-scheduled-entries .yt-entry-row');
        var result = [];
        rows.forEach(function (row) {
            var url = row.querySelector('.yt-entry-url').value.trim();
            if (url) {
                result.push({
                    Url: url,
                    DownloadPath: row.querySelector('.yt-entry-path').value.trim(),
                    MaxAgeDays: parseInt(row.querySelector('.yt-entry-maxage').value) || 0,
                    DeleteWatched: row.querySelector('.yt-entry-delwatched').checked
                });
            }
        });
        return result;
    }

    // -----------------------------------------------------------------------
    // Settings
    // -----------------------------------------------------------------------
    function loadConfig() {
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            document.getElementById('DownloadPath').value               = config.DownloadPath || '';
            document.getElementById('YtDlpBinaryPath').value            = config.YtDlpBinaryPath || '';
            document.getElementById('FfmpegBinaryPath').value           = config.FfmpegBinaryPath || '';
            var fmt = config.VideoFormat;
            var presetSel = document.getElementById('VideoFormatPreset');
            var knownPreset = false;
            for (var i = 0; i < presetSel.options.length; i++) {
                if (presetSel.options[i].value === fmt) { presetSel.value = fmt; knownPreset = true; break; }
            }
            if (!knownPreset) {
                presetSel.value = 'custom';
                document.getElementById('VideoFormat').value = fmt;
                document.getElementById('VideoFormatCustomContainer').style.display = '';
            } else {
                document.getElementById('VideoFormat').value = fmt;
                document.getElementById('VideoFormatCustomContainer').style.display = 'none';
            }
            document.getElementById('PreferredContainer').value         = config.PreferredContainer || 'mp4';
            document.getElementById('MaxConcurrentDownloads').value     = config.MaxConcurrentDownloads || 2;
            document.getElementById('DownloadRetryCount').value         = (config.DownloadRetryCount != null) ? config.DownloadRetryCount : 2;
            document.getElementById('PlaylistScanLimit').value          = (config.PlaylistScanLimit != null) ? config.PlaylistScanLimit : 50;
            document.getElementById('OrganiseByChannel').checked        = !!config.OrganiseByChannel;
            document.getElementById('ExcludeShorts').checked            = (config.ExcludeShorts != null) ? !!config.ExcludeShorts : true;
            document.getElementById('DownloadSubtitles').checked        = !!config.DownloadSubtitles;
            document.getElementById('SubtitleLanguages').value          = config.SubtitleLanguages || '';
            document.getElementById('DefaultAudioLanguage').value       = config.DefaultAudioLanguage || '';
            document.getElementById('CookiesFilePath').value            = config.CookiesFilePath || '';
            document.getElementById('WriteNfoFiles').checked            = !!config.WriteNfoFiles;
            document.getElementById('DownloadThumbnails').checked       = !!config.DownloadThumbnails;
            document.getElementById('TriggerLibraryScanAfterDownload').checked = !!config.TriggerLibraryScanAfterDownload;
            document.getElementById('DeleteWatchedManualVideos').checked = !!config.DeleteWatchedManualVideos;
            document.getElementById('EnableScheduledDownloads').checked = !!config.EnableScheduledDownloads;
            document.getElementById('DeleteWatchedScheduledVideos').checked = !!config.DeleteWatchedScheduledVideos;

            renderScheduledEntries(config.ScheduledEntries || []);
        });
    }

    function saveConfig() {
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            config.DownloadPath               = document.getElementById('DownloadPath').value;
            config.YtDlpBinaryPath            = document.getElementById('YtDlpBinaryPath').value;
            config.FfmpegBinaryPath           = document.getElementById('FfmpegBinaryPath').value;
            var preset = document.getElementById('VideoFormatPreset').value;
            config.VideoFormat = (preset === 'custom')
                ? document.getElementById('VideoFormat').value
                : preset;
            config.PreferredContainer         = document.getElementById('PreferredContainer').value;
            config.MaxConcurrentDownloads     = parseInt(document.getElementById('MaxConcurrentDownloads').value) || 2;
            var retryVal = parseInt(document.getElementById('DownloadRetryCount').value);
            config.DownloadRetryCount         = isNaN(retryVal) ? 2 : Math.max(0, retryVal);
            var scanVal = parseInt(document.getElementById('PlaylistScanLimit').value);
            config.PlaylistScanLimit          = isNaN(scanVal) ? 50 : Math.max(0, scanVal);
            config.OrganiseByChannel          = document.getElementById('OrganiseByChannel').checked;
            config.ExcludeShorts              = document.getElementById('ExcludeShorts').checked;
            config.DownloadSubtitles          = document.getElementById('DownloadSubtitles').checked;
            config.SubtitleLanguages          = document.getElementById('SubtitleLanguages').value;
            config.DefaultAudioLanguage       = document.getElementById('DefaultAudioLanguage').value;
            config.CookiesFilePath            = document.getElementById('CookiesFilePath').value;
            config.WriteNfoFiles              = document.getElementById('WriteNfoFiles').checked;
            config.DownloadThumbnails         = document.getElementById('DownloadThumbnails').checked;
            config.TriggerLibraryScanAfterDownload = document.getElementById('TriggerLibraryScanAfterDownload').checked;
            config.DeleteWatchedManualVideos  = document.getElementById('DeleteWatchedManualVideos').checked;
            config.EnableScheduledDownloads   = document.getElementById('EnableScheduledDownloads').checked;
            config.DeleteWatchedScheduledVideos = document.getElementById('DeleteWatchedScheduledVideos').checked;
            config.ScheduledEntries           = collectScheduledEntries();

            ApiClient.updatePluginConfiguration(PLUGIN_ID, config).then(function () {
                showToast('Einstellungen gespeichert.');
            });
        });
    }

    // -----------------------------------------------------------------------
    // Directory browser
    // -----------------------------------------------------------------------
    var _dirCurrentPath = '/';
    var _dirTargetInput = null;  // the <input> to write the selected path into

    function openDirBrowser(targetInput) {
        _dirTargetInput = targetInput;
        var overlay = document.getElementById('yt-dir-overlay');
        overlay.style.display = 'flex';
        var start = (targetInput ? targetInput.value.trim() : '') || '/';
        loadDirContents(start);
    }

    function closeDirBrowser() {
        document.getElementById('yt-dir-overlay').style.display = 'none';
        _dirTargetInput = null;
    }

    function loadDirContents(path) {
        _dirCurrentPath = path;
        document.getElementById('yt-dir-crumb').textContent = path;
        var list = document.getElementById('yt-dir-list');
        list.innerHTML = '<div class="yt-dir-item" style="color:#888;">Wird geladen\u2026</div>';

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

                // Back button (not at root)
                if (path && path !== '/') {
                    var idx = path.replace(/\/$/, '').lastIndexOf('/');
                    var parent = idx > 0 ? path.substring(0, idx) : '/';
                    var back = document.createElement('div');
                    back.className = 'yt-dir-item yt-dir-up';
                    back.textContent = '\u2b06 ..';
                    back.addEventListener('click', function () { loadDirContents(parent); });
                    list.appendChild(back);
                }

                if (!items.length && path !== '/') {
                    var empty = document.createElement('div');
                    empty.className = 'yt-dir-item';
                    empty.style.color = '#888';
                    empty.textContent = '(Leer)';
                    list.appendChild(empty);
                    return;
                }

                items.forEach(function (item) {
                    var itemPath = item.Path || item.Name;
                    var el = document.createElement('div');
                    el.className = 'yt-dir-item';
                    el.textContent = '\uD83D\uDCC1 ' + (item.Name || itemPath);
                    el.addEventListener('click', function () { loadDirContents(itemPath); });
                    list.appendChild(el);
                });
            })
            .catch(function (err) {
                list.innerHTML = '<div class="yt-dir-item" style="color:#f44336;">Fehler: ' + escHtml(String(err)) + '</div>';
            });
    }

    // -----------------------------------------------------------------------
    // Metadata preview
    // -----------------------------------------------------------------------
    function fetchMetadata() {
        var url = document.getElementById('yt-url-input').value.trim();
        if (!url) { showToast('Bitte eine URL eingeben.'); return; }

        showToast('Metadaten werden abgerufen\u2026');

        fetch(API_BASE + '/fetch-metadata', {
            method: 'POST',
            headers: apiHeaders(),
            body: JSON.stringify({ url: url })
        })
        .then(function (r) { return r.ok ? r.json() : Promise.reject(r.statusText); })
        .then(function (meta) {
            document.getElementById('yt-meta-title').textContent    = meta.Title || '';
            document.getElementById('yt-meta-channel').textContent  = meta.ChannelName || '';
            document.getElementById('yt-meta-date').textContent     = meta.UploadDate ? meta.UploadDate.substring(0, 10) : '';
            document.getElementById('yt-meta-duration').textContent = fmtDuration(meta.DurationSeconds);
            document.getElementById('yt-meta-desc').textContent     = (meta.Description || '').substring(0, 300);
            var thumb = document.getElementById('yt-meta-thumb');
            thumb.src = meta.ThumbnailUrl || '';
            thumb.style.display = meta.ThumbnailUrl ? '' : 'none';
            document.getElementById('yt-metadata-preview').style.display = 'block';
        })
        .catch(function (err) { showToast('Metadaten konnten nicht abgerufen werden: ' + err); });
    }

    // -----------------------------------------------------------------------
    // Download
    // -----------------------------------------------------------------------
    function enqueueDownload() {
        var url = document.getElementById('yt-url-input').value.trim();
        if (!url) { showToast('Bitte eine URL eingeben.'); return; }

        var isPlaylist  = document.getElementById('yt-is-playlist').checked;
        var dlPath      = document.getElementById('yt-dl-path').value.trim() || null;
        var forceCb     = document.getElementById('yt-force-download');
        var force       = forceCb && forceCb.checked;

        fetch(API_BASE + '/download', {
            method: 'POST',
            headers: apiHeaders(),
            body: JSON.stringify({ url: url, isPlaylist: isPlaylist, downloadPath: dlPath, force: force })
        })
        .then(function (r) {
            if (r.status === 201) {
                showToast('Zur Warteschlange hinzugef\u00fcgt.');
                document.getElementById('yt-url-input').value = '';
                document.getElementById('yt-dl-path').value  = '';
                document.getElementById('yt-metadata-preview').style.display = 'none';
                refreshJobs();
            } else {
                showToast('Fehler beim Hinzuf\u00fcgen: ' + r.statusText);
            }
        })
        .catch(function (err) { showToast('Error: ' + err); });
    }

    // -----------------------------------------------------------------------
    // Job list
    // -----------------------------------------------------------------------
    function refreshJobs() {
        fetch(API_BASE + '/jobs', { headers: apiHeaders() })
        .then(function (r) { return r.ok ? r.json() : Promise.reject(r.statusText); })
        .then(function (jobs) {
            renderJobs(jobs);
            updateStats(jobs);
        })
        .catch(function (err) { console.error('Jobs konnten nicht geladen werden:', err); });
    }

    function updateStats(jobs) {
        var q = 0, a = 0, c = 0, f = 0;
        jobs.forEach(function (j) {
            if (j.Status === 'Queued') q++;
            else if (['FetchingMetadata','Downloading','WritingMetadata'].indexOf(j.Status) >= 0) a++;
            else if (j.Status === 'Completed' || j.Status === 'CompletedWithErrors') c++;
            else f++;
        });
        document.getElementById('yt-queue-stats').textContent =
            'Wartend: ' + q + '  |  Aktiv: ' + a + '  |  Abgeschlossen: ' + c + '  |  Fehler/Abgebrochen: ' + f;
    }

    function renderJobs(jobs) {
        var tbody = document.getElementById('yt-job-tbody');
        tbody.innerHTML = '';

        if (!jobs.length) {
            var row = document.createElement('tr');
            row.innerHTML = '<td colspan="6" style="text-align:center;color:#888;">Keine Eintr\u00e4ge vorhanden.</td>';
            tbody.appendChild(row);
            return;
        }

        jobs.forEach(function (job) {
            var title = (job.Metadata && job.Metadata.Title) ? job.Metadata.Title : job.Url;
            var progressHtml = '';
            if (['FetchingMetadata','Downloading','WritingMetadata'].indexOf(job.Status) >= 0) {
                progressHtml = '<div class="yt-progress-bar-wrap"><div class="yt-progress-bar-fill" style="width:' +
                    Math.round(job.ProgressPercent) + '%"></div></div> ' + Math.round(job.ProgressPercent) + '%';
            } else if (job.Status === 'Completed' || job.Status === 'CompletedWithErrors') {
                progressHtml = '100%';
            }

            var actionHtml = '';
            var isActive = ['Queued','FetchingMetadata','Downloading','WritingMetadata'].indexOf(job.Status) >= 0;
            if (isActive) {
                actionHtml = '<button is="emby-button" data-id="' + job.Id + '" class="yt-cancel-btn raised" style="font-size:0.8em;padding:2px 8px;">Abbrechen</button>';
            }

            var runtimeHtml = '';
            if (job.StartedAt) {
                var endMs = job.CompletedAt ? new Date(job.CompletedAt).getTime() : Date.now();
                var elapsedSecs = Math.floor((endMs - new Date(job.StartedAt).getTime()) / 1000);
                runtimeHtml = fmtDuration(elapsedSecs);
            }

            var row = document.createElement('tr');
            row.innerHTML =
                '<td title="' + escHtml(job.Url) + '">' + escHtml(title.substring(0, 60)) + (title.length > 60 ? '\u2026' : '') + '</td>' +
                '<td><span class="yt-status-badge yt-status-' + job.Status + '">' + job.Status + '</span></td>' +
                '<td>' + progressHtml + '</td>' +
                '<td>' + runtimeHtml + '</td>' +
                '<td>' + fmtDate(job.CreatedAt) + '</td>' +
                '<td>' + actionHtml + '</td>';
            tbody.appendChild(row);
        });

        tbody.querySelectorAll('.yt-cancel-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                cancelJob(btn.dataset.id);
            });
        });
    }

    function cancelJob(id) {
        fetch(API_BASE + '/jobs/' + id, {
            method: 'DELETE',
            headers: apiHeaders()
        })
        .then(function (r) {
            if (r.status === 204) {
                showToast('Download abgebrochen.');
                refreshJobs();
            }
        });
    }

    // -----------------------------------------------------------------------
    // Tools check
    // -----------------------------------------------------------------------
    function checkTools() {
        var toolsUrl = window.location.origin + API_BASE + '/tools-check';
        console.log('[YT] tools-check URL:', toolsUrl);
        fetch(toolsUrl, { headers: apiHeaders() })
            .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status + ' ' + r.statusText); })
            .then(function (t) {
                document.getElementById('yt-tool-ytdlp').innerHTML = t.YtDlpAvailable
                    ? '<span class="yt-tool-ok">&#10003;</span> yt-dlp ' + escHtml(t.YtDlpVersion || '')
                    : '<span class="yt-tool-err">&#10007;</span> yt-dlp nicht gefunden' + (t.YtDlpError ? ' (' + escHtml(t.YtDlpError) + ')' : '');
                document.getElementById('yt-tool-ffmpeg').innerHTML = t.FfmpegAvailable
                    ? '<span class="yt-tool-ok">&#10003;</span> ' + escHtml((t.FfmpegVersion || '').split(' ').slice(0, 3).join(' '))
                    : '<span class="yt-tool-err">&#10007;</span> ffmpeg nicht gefunden' + (t.FfmpegError ? ' (' + escHtml(t.FfmpegError) + ')' : '');
            })
            .catch(function (err) {
                document.getElementById('yt-tool-ytdlp').innerHTML = '<span class="yt-tool-err">&#10007;</span> Fehler: ' + err;
                document.getElementById('yt-tool-ffmpeg').innerHTML = '';
                console.error('[YT] tools-check failed:', err);
            });
    }

    // -----------------------------------------------------------------------
    // Init
    // -----------------------------------------------------------------------
    function init() {
        console.log('[YT] init() called');
        loadConfig();
        refreshJobs();
        checkTools();

        document.getElementById('VideoFormatPreset').addEventListener('change', function () {
            var isCustom = this.value === 'custom';
            document.getElementById('VideoFormatCustomContainer').style.display = isCustom ? '' : 'none';
            if (!isCustom) document.getElementById('VideoFormat').value = this.value;
        });

        // Base path browser (targets DownloadPath input)
        document.getElementById('yt-browse-btn').addEventListener('click', function () {
            openDirBrowser(document.getElementById('DownloadPath'));
        });

        // Manual download path browser (targets yt-dl-path input)
        document.getElementById('yt-dl-browse-btn').addEventListener('click', function () {
            openDirBrowser(document.getElementById('yt-dl-path'));
        });

        // Dir browser confirm / cancel
        document.getElementById('yt-dir-select-btn').addEventListener('click', function () {
            if (_dirTargetInput) {
                _dirTargetInput.value = _dirCurrentPath;
            }
            closeDirBrowser();
        });
        document.getElementById('yt-dir-cancel-btn').addEventListener('click', closeDirBrowser);
        document.getElementById('yt-dir-overlay').addEventListener('click', function (e) {
            if (e.target === this) closeDirBrowser();
        });

        // Add scheduled entry row
        document.getElementById('yt-add-entry-btn').addEventListener('click', function () {
            addEntryRow('', '', 0, false);
        });

        document.getElementById('yt-clear-archive-btn').addEventListener('click', function () {
            if (!confirm('Download-Archiv wirklich zurücksetzen? Alle geplanten Playlist-Videos werden beim nächsten Task erneut heruntergeladen.'))
                return;
            fetch(API_BASE + '/archive', { method: 'DELETE', headers: apiHeaders() })
                .then(function (r) {
                    if (r.status === 204) showToast('Archiv wurde zurückgesetzt.');
                    else showToast('Fehler beim Zurücksetzen: ' + r.statusText);
                })
                .catch(function (err) { showToast('Fehler: ' + err); });
        });

        document.getElementById('yt-save-btn').addEventListener('click', saveConfig);
        document.getElementById('yt-fetch-meta-btn').addEventListener('click', fetchMetadata);
        document.getElementById('yt-download-btn').addEventListener('click', enqueueDownload);
        document.getElementById('yt-refresh-btn').addEventListener('click', refreshJobs);

        document.getElementById('yt-clear-finished-btn').addEventListener('click', function () {
            fetch(API_BASE + '/jobs/finished', { method: 'DELETE', headers: apiHeaders() })
                .then(function (r) { return r.ok ? r.json() : Promise.reject(r.statusText); })
                .then(function (count) {
                    showToast(count + ' Job(s) entfernt.');
                    refreshJobs();
                })
                .catch(function (err) { showToast('Fehler: ' + err); });
        });

        document.getElementById('yt-cancel-all-btn').addEventListener('click', function () {
            if (!confirm('Alle aktiven und wartenden Jobs wirklich abbrechen?')) return;
            fetch(API_BASE + '/jobs', { method: 'DELETE', headers: apiHeaders() })
                .then(function (r) { return r.ok ? r.json() : Promise.reject(r.statusText); })
                .then(function (count) {
                    showToast(count + ' Job(s) abgebrochen.');
                    refreshJobs();
                })
                .catch(function (err) { showToast('Fehler: ' + err); });
        });

        pollTimer = setInterval(refreshJobs, 5000);

        document.getElementById('JellyTubeConfigPage')
            .addEventListener('viewdestroy', function () {
                if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
            });
    }

    var _initialized = false;
    function safeInit() {
        if (_initialized) return;
        _initialized = true;
        console.log('[YT] safeInit() -> init()');
        init();
    }

    function attachAndInit() {
        var pageEl = document.getElementById('JellyTubeConfigPage');
        if (!pageEl) {
            setTimeout(attachAndInit, 50);
            return;
        }
        console.log('[YT] page element found');
        pageEl.addEventListener('pageshow', safeInit);
        pageEl.addEventListener('viewshow', safeInit);
        safeInit();
    }

    setTimeout(attachAndInit, 0);

}());
