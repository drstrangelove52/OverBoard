#pragma once
#include "lvgl.h"

// Montserrat Medium (SIL Open Font License) und Material Design Icons (Apache-2.0) als TrueType,
// zur Laufzeit von LVGL gerendert. Lizenzen siehe main/fonts/. Muss mit gehaltenem LVGL-Lock aufgerufen werden.
void fonts_init(void);

// Textschrift in Pixelgroesse: 14, 20, 28, 36, 48, 64 oder 120; sonst die naechstgroessere.
const lv_font_t *ob_font(int px);
// Symbolschrift (Material Design Icons): 24, 28, 36, 48 oder 64.
const lv_font_t *ob_icon_font(int px);
