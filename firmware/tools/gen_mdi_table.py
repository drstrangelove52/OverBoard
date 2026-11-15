#!/usr/bin/env python3
"""Erzeugt overboard/main/mdi_table.c (Symbolname -> Unicode-Codepunkt) aus materialdesignicons.css.

Quelle: npm-Paket @mdi/font 7.4.47 (Pictogrammers, Apache-2.0)
  https://cdn.jsdelivr.net/npm/@mdi/font@7.4.47/css/materialdesignicons.css
  https://cdn.jsdelivr.net/npm/@mdi/font@7.4.47/fonts/materialdesignicons-webfont.ttf

Aufruf:  python tools/gen_mdi_table.py PFAD/materialdesignicons.css overboard/main/mdi_table.c
"""
import re
import sys

BACKSLASH = chr(92)
PATTERN = re.compile(
    r"\.mdi-([a-z0-9-]+)::before\s*\{\s*content:\s*\"" + re.escape(BACKSLASH) + r"([0-9A-Fa-f]+)\""
)

css, out = sys.argv[1], sys.argv[2]
text = open(css, encoding="utf-8").read()
icons = {name: int(cp, 16) for name, cp in PATTERN.findall(text)}
items = sorted(icons.items())  # sortiert fuer die binaere Suche
with open(out, "w", encoding="utf-8", newline="\n") as f:
    f.write("// Erzeugt von tools/gen_mdi_table.py aus @mdi/font 7.4.47 (Apache-2.0). Nicht von Hand aendern.\n")
    f.write('#include "mdi.h"\n\n')
    f.write("const mdi_entry_t mdi_table[] = {\n")
    for name, cp in items:
        f.write('    { "%s", 0x%X },\n' % (name, cp))
    f.write("};\n")
    f.write("const int mdi_table_len = %d;\n" % len(items))
print("%d Symbole geschrieben nach %s" % (len(items), out))
