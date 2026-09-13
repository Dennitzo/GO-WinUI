# highlight.js 11.11.1

Pinned, locally bundled browser common build and PowerShell grammar, BSD-3-Clause (see LICENSE).
No runtime download or network dependency.

Sources:
- https://github.com/highlightjs/cdn-release/tree/11.11.1/build
- https://github.com/highlightjs/highlight.js/tree/11.11.1

GO uses the pinned token tree in `code-highlighting.js` and creates only text nodes and token spans.
Integration tests verify exact source preservation, grammar colors, unfinished streamed fences and inert HTML.
