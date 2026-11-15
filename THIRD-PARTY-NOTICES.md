# Model logo artwork

Model and author marks identify the models available in Amarin Admin AI. They do not imply sponsorship or endorsement. Trademarks and author avatars remain the property of their respective owners.

The source URL, local file and SHA256 of each additional image are recorded in [tools/model-logos/assets.json](tools/model-logos/assets.json). The source images are shipped in the repository; the corresponding WPF drawings or bitmap resources are embedded in the application. This notice is also embedded in the application assembly as `Amarin.ThirdPartyNotices.md`.

## Lobe Icons

Source: <https://github.com/lobehub/lobe-icons>

Pinned revision: `ea434b96e75813517b377825e01c0df61dee8836`.

The additional assets marked `Lobe Icons` in the manifest are distributed under this license:

MIT License

Copyright (c) 2023 LobeHub

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Additional author artwork

The remaining images come from public author profiles on Hugging Face, the authors' official websites, and OpenRouter's author icon assets. Their individual sources are recorded in the manifest. They are used only to identify the corresponding model author. They are not represented as MIT-licensed LobeHub artwork or as original Amarin artwork.

Writer light and dark icons are the original variants provided at <https://writer.com/newsroom/>. Their colors and geometry are preserved. Ricursive light and dark icons are the original variants linked by <https://www.ricursive.com/>.

## Build-time converter

The optional development tool in `tools/model-logos/VectorImporter` restores [SharpVectors.Wpf](https://github.com/ElinamLLC/SharpVectors), version 1.8.6, under its BSD-3-Clause license. SharpVectors is used to prepare ordinary WPF drawing resources. It is not a dependency of the shipped application.
