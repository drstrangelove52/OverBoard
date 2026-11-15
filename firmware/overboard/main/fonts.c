#include "fonts.h"

#include "esp_log.h"

static const char *TAG = "fonts";

extern const uint8_t text_ttf_start[] asm("_binary_Montserrat_Medium_ttf_start");
extern const uint8_t text_ttf_end[] asm("_binary_Montserrat_Medium_ttf_end");
extern const uint8_t icon_ttf_start[] asm("_binary_materialdesignicons_webfont_ttf_start");
extern const uint8_t icon_ttf_end[] asm("_binary_materialdesignicons_webfont_ttf_end");

static const int k_text_sizes[] = { 14, 20, 28, 36, 48, 64, 120 };
static const int k_icon_sizes[] = { 24, 28, 36, 48, 64 };
#define N_TEXT ((int)(sizeof(k_text_sizes) / sizeof(k_text_sizes[0])))
#define N_ICON ((int)(sizeof(k_icon_sizes) / sizeof(k_icon_sizes[0])))
static lv_font_t *s_text[N_TEXT];
static lv_font_t *s_icon[N_ICON];

void fonts_init(void)
{
    for (int i = 0; i < N_TEXT; i++) {
        s_text[i] = lv_tiny_ttf_create_data(text_ttf_start, text_ttf_end - text_ttf_start, k_text_sizes[i]);
        if (!s_text[i]) ESP_LOGE(TAG, "Textschrift %d px konnte nicht erzeugt werden", k_text_sizes[i]);
    }
    for (int i = 0; i < N_ICON; i++) {
        s_icon[i] = lv_tiny_ttf_create_data(icon_ttf_start, icon_ttf_end - icon_ttf_start, k_icon_sizes[i]);
        if (!s_icon[i]) ESP_LOGE(TAG, "Symbolschrift %d px konnte nicht erzeugt werden", k_icon_sizes[i]);
    }
    ESP_LOGI(TAG, "Schriften bereit (%d Text-, %d Symbolgroessen)", N_TEXT, N_ICON);
}

const lv_font_t *ob_font(int px)
{
    for (int i = 0; i < N_TEXT; i++) {
        if (k_text_sizes[i] >= px && s_text[i]) return s_text[i];
    }
    return LV_FONT_DEFAULT;
}

const lv_font_t *ob_icon_font(int px)
{
    for (int i = 0; i < N_ICON; i++) {
        if (k_icon_sizes[i] >= px && s_icon[i]) return s_icon[i];
    }
    return LV_FONT_DEFAULT;
}
