/* Injected only by scripts/dev.py. Never embedded in the released plugin. */
(function () {
    'use strict';
    var revision;
    var banner;
    function show(message) {
        if (!banner) {
            banner = document.createElement('div');
            banner.setAttribute('role', 'status');
            banner.style.cssText = 'position:fixed;bottom:12px;left:12px;right:12px;z-index:100000;padding:12px;background:#202020;color:white;border:1px solid #888;border-radius:4px;font:14px sans-serif';
            document.body.appendChild(banner);
        }
        banner.textContent = message;
        banner.hidden = !message;
    }
    function restoreTab() {
        var id = sessionStorage.getItem('meta-tagger-dev-tab');
        var tab = id && document.getElementById(id);
        if (tab && document.querySelector('#MetaTaggerConfigPage #SaveSettingsButton:not(:disabled)')) {
            sessionStorage.removeItem('meta-tagger-dev-tab');
            tab.click();
        }
    }
    async function poll() {
        try {
            restoreTab();
            var response = await fetch('/__meta_tagger_dev/status', { cache: 'no-store' });
            if (!response.ok) { throw new Error('Dev server unavailable'); }
            var state = await response.json();
            if (revision === undefined) { revision = state.revision; }
            if (!state.ready) { show(state.status); }
            else if (revision !== state.revision) {
                var dirty = document.querySelector('#SettingsFeedback[data-unsaved="true"]');
                if (dirty) { show('Development update ready. Save your settings to reload, or reload the page to discard your edits.'); }
                else {
                    var tab = document.querySelector('#MetaTaggerConfigPage [role="tab"][aria-selected="true"]');
                    if (tab) { sessionStorage.setItem('meta-tagger-dev-tab', tab.id); }
                    location.reload();
                    return;
                }
            } else { show(''); }
        } catch (_) { show('Development server disconnected. Restart ./scripts/dev.sh to reconnect.'); }
        setTimeout(poll, 1000);
    }
    poll();
}());
