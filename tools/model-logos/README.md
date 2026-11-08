# Model logos

The application ships its logos as local WPF resources. It does not fetch images while a model picker or a chat is being displayed. The original handwritten entries in `AiLogos.Dark.xaml` and `AiLogos.Light.xaml`, including `Auto`, stay outside the generated block.

## Import

Run on Windows with .NET 10 from the repository root:

```powershell
python tools/model-logos/sync.py --download
./tools/model-logos/import.ps1
./tools/model-logos/import.ps1 -Check
python tools/model-logos/sync.py --report release/model-logo-coverage.json
```

The downloader requires Python 3.9 or later and the standard library only. All downloaded images have a recorded SHA256. Existing matching files are used offline. An upstream change fails until it has been reviewed and accepted with `--download --accept-changes`; HTTPS, image responses and a 2 MiB limit are enforced. Original SVGs and bitmaps are kept unchanged.

`python tools/model-logos/sync.py --refresh-catalog` checks the current public OpenRouter text and Decisions catalog without an API key. If it finds an unknown author, it lists the missing models and retains the previous offline snapshot. Add the author mapping and a reviewed source image, regenerate resources, then rerun the command. The application never reads this development snapshot or downloads artwork.

`Amarin.Core/Providers/ModelLogoCatalog.json` is the shared mapping source. Exact model overrides precede author-scoped family rules and author defaults. Legacy IDs without a namespace use ordered family rules. Transport prefixes, `~latest` aliases and variant suffixes do not change the logo. Unknown namespaces keep the normal letter fallback rather than borrowing a different author's brand from a substring.

Non-WPF regression tests compare the checked-in snapshot with the actual Core resolver. WPF tests verify every declared mapping key and snapshot model against both dictionaries, then render every image at 15/20 DIP and 96/192 DPI. To write the contact sheets without showing an application window:

```powershell
$env:AMARIN_MODEL_LOGO_SHOTS = Join-Path $PWD 'release/model-logo-shots'
dotnet test Amarin.AdminAI.Tests/Amarin.AdminAI.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ModelLogoRenderingTests'
```

`-Check` performs the same conversion without writing either dictionary or copying bitmap resources. It fails when checked-in output differs from the local source assets. Both themes and bitmap copies are prepared and validated before any output is written. No download is performed by the importer.

The separate `VectorImporter` project pins `SharpVectors.Wpf` to version 1.8.6. Neither the application nor Core references that project or its SVG dependency. The generated XAML contains WPF drawing types only. SVG paths, transforms, clipping, opacity and gradients are converted by SharpVectors rather than a custom subset renderer. SVG text, embedded images, external references and unreviewed elements are rejected, so a failed conversion needs an explicit review instead of silently dropping artwork.

The exact standard SVG 1.1 public doctype declaration is removed in memory before parsing. DTD processing stays prohibited and no external DTD is fetched. Other doctypes and entity declarations are rejected.

## Manifest

`assets.json` has this shape:

```json
{
  "schemaVersion": 1,
  "assets": [
    {
      "key": "Liquid",
      "file": "assets/liquid.svg",
      "kind": "svg",
      "monochrome": true,
      "sourceUrl": "https://example.com/pinned-source.svg",
      "sha256": "reviewed-file-sha256",
      "license": "MIT"
    }
  ]
}
```

`file` and optional `darkFile` are local paths relative to this directory. `kind` is `svg` or `bitmap` (`png` is also accepted). Keys must be unique and must not replace a handwritten dictionary entry. Additional source and licensing metadata is preserved in the manifest but is not used by the converter. `currentColor` becomes white in the dark theme and `#16161A` in the light theme. `monochrome: true` also sets the default SVG fill to that theme's ink; explicit brand colors remain unchanged.

`inkColors` can explicitly select fixed SVG paint values to replace with the theme ink, for example `"inkColors": ["black", "#000000"]`. Matching is exact and case-insensitive in fill, stroke, color and stop-color attributes or CSS declarations. Other colors stay intact. `foreground` can override the ink for both themes, for example `"foreground": "#16161A"` for a dark shape drawn over an artwork's own light background. These transformations operate on an in-memory document; downloaded source files and their hashes are unchanged.

For a valid root `viewBox`, the in-memory SVG width and height are normalized to the viewBox dimensions. This keeps export dimensions such as 1000 by 1000 from adding empty padding around a logo whose actual coordinate grid is 335 by 335. Invalid or empty viewBox values are rejected.

`stripColorSchemeStyle: true` is an explicit exception for reviewed artwork whose stylesheet consists solely of `svg { color: #... }` and an equivalent `prefers-color-scheme: dark` rule. Once `currentColor` is replaced with explicit theme ink, those rules are unnecessary. The importer validates that exact CSS shape before removing the stylesheet from its in-memory document. Other media rules are rejected instead of being discarded or handed to a CSS parser that may stall.

Bitmap entries refer to `/Amarin Admin AI;component/Assets/ModelLogos/<filename>`. The importer copies the reviewed local image into that application resource directory and verifies the copy in `-Check` mode. It rejects different bitmap sources with the same filename. PNG, JPG/JPEG, ICO, BMP, GIF and TIFF are accepted; WebP is not a standard WPF format. An optional `darkFile` can select separate dark artwork. The application project must include those files as WPF `Resource` items.

Commit the source assets, their original license notices, the manifest and regenerated dictionaries together. Pin upstream versions or commits; an unversioned CDN URL is not a reproducible source. Check both themes at the actual 15 and 20 pixel display sizes after updating artwork.

## Upstream references

- [Lobe Icons](https://github.com/lobehub/lobe-icons), including static SVG and light/dark PNG packages.
- [SharpVectors FileSvgReader](https://elinamllc.github.io/SharpVectors/api/SharpVectors.Converters.FileSvgReader.html).
- [SharpVectors drawing settings](https://elinamllc.github.io/SharpVectors/api/SharpVectors.Renderers.Wpf.WpfDrawingSettings.html).
