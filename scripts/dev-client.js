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
    // Older development clients remembered tabs that the workspace no longer has.
    try { sessionStorage.removeItem('meta-tagger-dev-tab'); } catch (_) { /* Storage may be unavailable. */ }
    async function poll() {
        try {
            var response = await fetch('/__meta_tagger_dev/status', { cache: 'no-store' });
            if (!response.ok) { throw new Error('Dev server unavailable'); }
            var state = await response.json();
            if (revision === undefined) { revision = state.revision; }
            if (!state.ready) { show(state.status); }
            else if (revision !== state.revision) {
                var dirty = document.querySelector('#SettingsFeedback[data-unsaved="true"]');
                if (dirty) { show('Development update ready. Save your settings to reload, or reload the page to discard your edits.'); }
                else {
                    location.reload();
                    return;
                }
            } else { show(''); }
        } catch (_) { show('Development server disconnected. Restart ./scripts/dev.sh to reconnect.'); }
        setTimeout(poll, 1000);
    }
    poll();
}());
