# Hyphenation patterns: sources and licences

The `*.hyph.gz` files in this directory are Liang (TeX) hyphenation patterns from the
[hyph-utf8](https://github.com/hyphenation/tex-hyphen) project, commit
`5684c0f51c0b81133db2efbe60a408b4155a3ff5` (2026-02-24), converted by `tools/generate_hyphenation_patterns.py`.
They are not covered by RaiCore.Pdf's Apache-2.0 licence: each keeps its own licence, listed below. Every file also
starts with the original file's header (title, copyright, licence) unchanged.

Only languages whose patterns may be redistributed, modified (converted), in a permissively licensed package are
included. Patterns under the LPPL alone (such as Swedish), the GPL (such as Czech) or unclear terms are not bundled;
they can be loaded at run time with `Hyphenator.FromPatterns` and `Hyphenator.Register`.

| Resource | Language | Source file | Copyright | Licence |
| --- | --- | --- | --- | --- |
| `en-us` | English (US) | `hyph-en-us.tex` | Copyright (C) 1990, 2004, 2005 Gerard D.C. Kuiken | Permissive notice ([FSFAP](https://spdx.org/licenses/FSFAP.html) text) |
| `en-gb` | English (UK) | `hyph-en-gb.tex` | Copyright (C) 1992, 1996, 2005, 2016 Dominik Wujastyk, Graham Toal | MIT |
| `de` | German (reformed spelling, 1996/2006) | `hyph-de-1996.tex` | Copyright (c) 2013-2024 Deutschsprachige Trennmustermannschaft | MIT |
| `da` | Danish | `hyph-da.tex` | Copyright (C) 1994 Frank Jensen | MIT (offered as LPPL 1.3+ or MIT; used under MIT) |
| `nl` | Dutch | `hyph-nl.tex` | Copyright (C) 1996 Piet Tutelaers | MIT |
| `fr` | French | `hyph-fr.tex` | Copyright (C) 1994-2002 Daniel Flipo, Bernard Gaulle, 2016 Arthur Reutenauer | MIT |
| `es` | Spanish | `hyph-es.tex` | Copyright (C) 1993, 1997 Javier Bezos, 2001-2019 Javier Bezos, CervanTeX | MIT |
| `it` | Italian | `hyph-it.tex` | Copyright (C) 2008-2011 Claudio Beccari | MIT (offered as LPPL 1.3+ or MIT; used under MIT) |
| `pt` | Portuguese | `hyph-pt.tex` | Copyright (C) 1987, 1994, 1996, 2015 Pedro J. de Rezende, 1996, 2015 J. Joao Dias Almeida, 2024 Leonardo Araujo and Aline Benevides | BSD 3-clause |
| `pl` | Polish | `hyph-pl.tex` | Copyright (C) 1987-1995 Hanna Kołodziejska, Bogusław Jackowski, Marek Ryćko | MIT (offered under several licences; used under MIT) |
| `nb` | Norwegian Bokmål | `hyph-nb.tex`, patterns from `hyph-no.tex` | Copyright (C) 2004-2005 Rune Kleveland, Ole Michael Selberg, 2007 Karl Ove Hufthammer | Permissive notice ([FSFAP](https://spdx.org/licenses/FSFAP.html) text) |
| `nn` | Norwegian Nynorsk | `hyph-nn.tex`, patterns from `hyph-no.tex` | Copyright (C) 2007 Karl Ove Hufthammer; 2004, 2005 Rune Kleveland, Ole Michael Selberg | Permissive notice ([FSFAP](https://spdx.org/licenses/FSFAP.html) text) |

The British English patterns were made from a list of hyphenated words that Oxford University Press made available to
the authors; the list itself is not included (and may not be redistributed), and the authors state that the patterns
may be freely distributed.

## Permissive notice (English US, Norwegian)

> Copying and distribution of this file, with or without modification, are permitted in any medium without royalty
> provided the copyright notice and this notice are preserved.

## MIT licence (English UK, German, Danish, Dutch, French, Spanish, Italian, Polish)

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
> documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
> rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
> permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of
> the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
> WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
> COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
> OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## BSD 3-clause licence (Portuguese)

> Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
> following conditions are met:
>
> * Redistributions of source code must retain the above copyright notice, this list of conditions and the following
>   disclaimer.
> * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
>   following disclaimer in the documentation and/or other materials provided with the distribution.
> * Neither the name of the University of Campinas, of the University of Minho nor the names of its contributors may
>   be used to endorse or promote products derived from this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
> INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
> DISCLAIMED. IN NO EVENT SHALL PEDRO J. DE REZENDE OR J.JOAO DIAS ALMEIDA BE LIABLE FOR ANY DIRECT, INDIRECT,
> INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE
> GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
> LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
> OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
