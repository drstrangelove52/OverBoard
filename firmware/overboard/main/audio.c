#include "audio.h"

#include <math.h>
#include <string.h>

#include "driver/gpio.h"
#include "driver/i2s_std.h"
#include "esp_check.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"
#include "config.h"

static const char *TAG = "audio";

// Pins laut Elecrow-Handbuch (V1.2): I2S1, Verstaerker-Enable ist aktiv low
#define PIN_BCLK 22
#define PIN_WS   21
#define PIN_DOUT 23
#define PIN_AMP  30

#define SAMPLE_RATE      16000
#define DEFAULT_VOLUME   40      // Prozent; der Verstaerker ist leise, 100 = Vollaussteuerung
#define CHUNK_PAIRS      512

typedef struct { uint16_t hz; uint16_t ms; } step_t;   // hz = 0: Pause, ms = 0: Ende

static const step_t SND_INFO[]  = { {880, 110}, {1320, 170}, {0, 0} };
static const step_t SND_OK[]    = { {660, 110}, {880, 110}, {1100, 200}, {0, 0} };
static const step_t SND_WARN[]  = { {1000, 150}, {0, 100}, {1000, 150}, {0, 100}, {1000, 150}, {0, 0} };
static const step_t SND_ALARM[] = { {800, 200}, {1300, 200}, {800, 200}, {1300, 200}, {800, 200}, {1300, 200}, {0, 0} };

typedef struct { const step_t *steps; int volume; bool click; } job_t;   // click: steps wird nicht verwendet

static i2s_chan_handle_t s_tx;
static QueueHandle_t s_queue;
static int16_t s_buf[CHUNK_PAIRS * 2];

static esp_err_t audio_init_hw(void)
{
    const gpio_config_t amp = { .pin_bit_mask = 1ULL << PIN_AMP, .mode = GPIO_MODE_OUTPUT };
    ESP_RETURN_ON_ERROR(gpio_config(&amp), TAG, "amp gpio");
    gpio_set_level(PIN_AMP, 1);   // aus

    i2s_chan_config_t cc = { .id = I2S_NUM_1, .role = I2S_ROLE_MASTER, .dma_desc_num = 6, .dma_frame_num = 256,
                             .auto_clear = true };
    ESP_RETURN_ON_ERROR(i2s_new_channel(&cc, &s_tx, NULL), TAG, "i2s channel");
    i2s_std_config_t std = {
        .clk_cfg = { .sample_rate_hz = SAMPLE_RATE, .clk_src = I2S_CLK_SRC_DEFAULT, .mclk_multiple = I2S_MCLK_MULTIPLE_256 },
        .slot_cfg = { .data_bit_width = I2S_DATA_BIT_WIDTH_16BIT, .slot_bit_width = I2S_SLOT_BIT_WIDTH_AUTO,
                      .slot_mode = I2S_SLOT_MODE_STEREO, .slot_mask = I2S_STD_SLOT_BOTH,
                      .ws_width = I2S_DATA_BIT_WIDTH_16BIT, .ws_pol = false, .bit_shift = true,
                      .left_align = true, .big_endian = false, .bit_order_lsb = false },
        .gpio_cfg = { .mclk = I2S_GPIO_UNUSED, .bclk = PIN_BCLK, .ws = PIN_WS, .dout = PIN_DOUT, .din = I2S_GPIO_UNUSED },
    };
    ESP_RETURN_ON_ERROR(i2s_channel_init_std_mode(s_tx, &std), TAG, "i2s std");
    return i2s_channel_enable(s_tx);
}

static void flush(size_t pairs)
{
    size_t written = 0;
    i2s_channel_write(s_tx, s_buf, pairs * 2 * sizeof(int16_t), &written, portMAX_DELAY);
}

// Verstaerker bleibt nach einem Ton noch kurz an: so setzt der naechste Klick ohne Einschaltverzoegerung ein.
#define AMP_HOLD_MS 800
static bool s_amp_on;

static void amp_set(bool on)
{
    if (on == s_amp_on) return;
    gpio_set_level(PIN_AMP, on ? 0 : 1);
    s_amp_on = on;
    if (on) vTaskDelay(pdMS_TO_TICKS(30));   // Einschwingzeit des Verstaerkers
}

static void flush_zero(int chunks)
{
    memset(s_buf, 0, sizeof(s_buf));
    for (int i = 0; i < chunks; i++) flush(CHUNK_PAIRS);
}

static void play_steps(const step_t *steps, int volume)
{
    if (volume < 0) volume = ob_config()->sound_volume;
    if (volume > 100) volume = 100;
    if (volume == 0) return;
    const float amp = 32000.f * volume / 100.f;
    const int att = SAMPLE_RATE / 125;   // 8 ms Ein-/Ausblenden gegen Knacken

    amp_set(true);

    size_t fill = 0;
    float phase = 0;
    for (const step_t *s = steps; s->ms; s++) {
        int n = SAMPLE_RATE * s->ms / 1000;
        float inc = 2.f * (float)M_PI * s->hz / SAMPLE_RATE;
        for (int i = 0; i < n; i++) {
            float env = 1.f;
            if (i < att) env = (float)i / att;
            else if (n - i < att) env = (float)(n - i) / att;
            int16_t v = s->hz ? (int16_t)(sinf(phase) * amp * env) : 0;
            phase += inc;
            if (phase > 2.f * (float)M_PI) phase -= 2.f * (float)M_PI;
            s_buf[fill * 2] = v;
            s_buf[fill * 2 + 1] = v;
            if (++fill == CHUNK_PAIRS) { flush(fill); fill = 0; }
        }
    }
    if (fill) flush(fill);
    flush_zero(2);                       // Stille hinterher
}

// Tastenklick: ein kurzes, weiches "Tick" wie ein Fingertipp auf Holz. Zwei abklingende Sinustoene (900 Hz und ein
// unharmonischer Oberton), sehr kurzer Anstieg, schnelles Abklingen. Etwa 45 ms.
static void play_click(int volume)
{
    if (volume > 100) volume = 100;
    if (volume <= 0) return;
    const float amp = 32000.f * volume / 100.f;
    const int n = SAMPLE_RATE * 45 / 1000;
    const int rise = SAMPLE_RATE / 1500;           // ca. 0,7 ms Anstieg
    const float f1 = 900.f, f2 = 2300.f;           // Grundton und Oberton
    const float tau1 = 0.007f, tau2 = 0.003f;      // Abklingzeiten in Sekunden

    amp_set(true);
    // Der Puffer fasst nur CHUNK_PAIRS Paare (512): in Stuecken schreiben, nie ueber das Ende hinaus
    size_t fill = 0;
    for (int i = 0; i < n; i++) {
        float t = (float)i / SAMPLE_RATE;
        float env = (i < rise) ? 0.5f * (1.f - cosf((float)M_PI * i / rise)) : 1.f;
        float v = sinf(2.f * (float)M_PI * f1 * t) * expf(-t / tau1) + 0.35f * sinf(2.f * (float)M_PI * f2 * t) * expf(-t / tau2);
        int16_t s16 = (int16_t)(v * amp * 0.75f * env);
        s_buf[fill * 2] = s16;
        s_buf[fill * 2 + 1] = s16;
        if (++fill == CHUNK_PAIRS) { flush(fill); fill = 0; }
    }
    if (fill) flush(fill);
    flush_zero(1);
}

static void audio_task(void *arg)
{
    job_t job;
    for (;;) {
        if (xQueueReceive(s_queue, &job, s_amp_on ? pdMS_TO_TICKS(AMP_HOLD_MS) : portMAX_DELAY) == pdTRUE) {
            if (job.click) play_click(job.volume);
            else play_steps(job.steps, job.volume);
        } else {
            amp_set(false);              // laenger nichts mehr zu spielen: Verstaerker aus
        }
    }
}

esp_err_t audio_init(void)
{
    ESP_RETURN_ON_ERROR(audio_init_hw(), TAG, "Hardware");
    s_queue = xQueueCreate(4, sizeof(job_t));
    ESP_RETURN_ON_FALSE(s_queue, ESP_ERR_NO_MEM, TAG, "queue");
    ESP_RETURN_ON_FALSE(xTaskCreate(audio_task, "audio", 6144, NULL, 5, NULL) == pdPASS, ESP_FAIL, TAG, "task");
    ESP_LOGI(TAG, "Audio bereit");
    return ESP_OK;
}

void audio_play(const char *sound, int volume)
{
    if (!s_queue || !sound) return;
    const step_t *steps = NULL;
    if (!strcmp(sound, "click")) {
        job_t job = { .click = true, .volume = volume < 0 ? ob_config()->click_volume : volume };
        xQueueSend(s_queue, &job, 0);
        return;
    }
    if (!strcmp(sound, "info")) steps = SND_INFO;
    else if (!strcmp(sound, "ok")) steps = SND_OK;
    else if (!strcmp(sound, "warn")) steps = SND_WARN;
    else if (!strcmp(sound, "alarm")) steps = SND_ALARM;
    if (!steps) return;
    job_t job = { .steps = steps, .volume = volume, .click = false };
    if (xQueueSend(s_queue, &job, 0) != pdTRUE) ESP_LOGW(TAG, "Tonwarteschlange voll, '%s' verworfen", sound);
}

void audio_click(void)
{
    const ob_config_t *cfg = ob_config();
    if (!s_queue || !cfg->click || cfg->click_volume <= 0) return;
    job_t job = { .click = true, .volume = cfg->click_volume };
    xQueueSend(s_queue, &job, 0);       // voll: Klick verwerfen statt warten
}
