# Third-party notices

Archify.Net's own license is in [`LICENSE`](LICENSE). It carries the archify and Cocoon AI notices. The notices below
cover code from other projects.

## Pele

Pele (https://github.com/obsidianmd/pele) is the chart renderer that archify 3.2.0 vendors, at version 1.3.0. This
notice covers:

- the C# port of Pele 1.3.0 in `src/Archify.Net.Core/Pele/`;
- `tools/pele-internals/pele-internals.mjs`, a bundle of Pele 1.3.0 source that the fixture capture uses;
- the seed inputs under `tools/chart-seeds/` taken from Pele's `tests/corpus`, `tests/compat` and `tests/render`, and
  Pele's `random` and `mutator` (`tests/support/corpus.ts`), copied into `tools/capture-fixtures.mjs`.

Pele's zoom controls, and the Lucide icon its license also names, are not ported.

```
MIT License

Copyright (c) 2026 kepano

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
```

## V8 and fdlibm

`src/Archify.Net.Core/Js/JsMath.cs` ports V8 13.6.233.17's `src/base/ieee754.cc`, which is adapted from Sun's fdlibm.
The file's header keeps the Sun Microsystems notice and the V8 BSD-style notice.
