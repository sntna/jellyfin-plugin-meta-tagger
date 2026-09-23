# Meta Tagger artwork

| Asset | Purpose |
| --- | --- |
| [logo-mark.svg](assets/logo-mark.svg) | Standalone vector mark |
| [logo-light.svg](assets/logo-light.svg) | Light lettering for dark backgrounds |
| [logo-dark.svg](assets/logo-dark.svg) | Dark lettering for light backgrounds |
| [plugin.png](assets/plugin.png) | Jellyfin catalog and package image |

The SVG logos use outlined lettering and need no installed fonts. Preserve
their proportions and transparent holes when resizing.

The dashboard embeds the light and dark logos. After replacing either SVG, run:

```sh
node docs/brand/render-assets.cjs
./scripts/build-and-test.sh
```

The script updates the dashboard's embedded logos. It does not modify the
source SVGs or regenerate the supplied PNG. The package builder and disposable
Jellyfin installer use `assets/plugin.png` as `meta-tagger.png`.

Dashboard styles live in
[`configPage.html`](../../Jellyfin.Plugin.MetaTagger/Configuration/configPage.html).
Controls inherit Jellyfin's font; metadata uses a system monospace stack.
The palette uses charcoal `#151A1E`, ivory `#F2EFE7`, cyan `#69CED6`, and slate
`#829398`, with separate colors and text for warnings, errors, and removals.
