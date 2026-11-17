#include "board.h"

#include "driver/gpio.h"
#include "driver/i2c_master.h"
#include "driver/ledc.h"
#include "esp_check.h"
#include "esp_lcd_ek79007.h"
#include "esp_lcd_mipi_dsi.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_touch_gt911.h"
#include "esp_ldo_regulator.h"
#include "esp_log.h"
#include "esp_lvgl_port.h"

static const char *TAG = "board";

// Pins laut Elecrow-Handbuch (V1.2)
#define PIN_I2C_SDA   45
#define PIN_I2C_SCL   46
#define PIN_TOUCH_RST 40
#define PIN_TOUCH_INT 42
#define PIN_BACKLIGHT 31
#define BL_PWM_HZ     30000
#define BITS_PER_PIXEL 16

static esp_ldo_channel_handle_t s_ldo3, s_ldo4;
static i2c_master_bus_handle_t s_i2c;
static esp_lcd_touch_handle_t s_touch;
static esp_lcd_dsi_bus_handle_t s_dsi_bus;
static esp_lcd_panel_io_handle_t s_dbi_io;
static esp_lcd_panel_handle_t s_panel;
static lv_display_t *s_disp;

static esp_err_t power_init(void)
{
    // LDO3 = 2,5 V (IO-Domaene), LDO4 = 3,3 V (Panel analog)
    esp_ldo_channel_config_t c3 = { .chan_id = 3, .voltage_mv = 2500 };
    ESP_RETURN_ON_ERROR(esp_ldo_acquire_channel(&c3, &s_ldo3), TAG, "LDO3");
    esp_ldo_channel_config_t c4 = { .chan_id = 4, .voltage_mv = 3300 };
    ESP_RETURN_ON_ERROR(esp_ldo_acquire_channel(&c4, &s_ldo4), TAG, "LDO4");
    return ESP_OK;
}

static esp_err_t i2c_init(void)
{
    i2c_master_bus_config_t cfg = {
        .i2c_port = 0,
        .sda_io_num = PIN_I2C_SDA,
        .scl_io_num = PIN_I2C_SCL,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    return i2c_new_master_bus(&cfg, &s_i2c);
}

static esp_err_t touch_init(void)
{
    esp_lcd_touch_config_t tp_cfg = {
        .x_max = OB_LCD_H,
        .y_max = OB_LCD_V,
        .rst_gpio_num = PIN_TOUCH_RST,
        .int_gpio_num = PIN_TOUCH_INT,
        .levels = { .reset = 0, .interrupt = 0 },
        .flags = { .swap_xy = false, .mirror_x = false, .mirror_y = false },
    };
    // GT911 hat zwei moegliche I2C-Adressen, die zweite als Rueckfall probieren
    uint8_t addrs[2] = { ESP_LCD_TOUCH_IO_I2C_GT911_ADDRESS, ESP_LCD_TOUCH_IO_I2C_GT911_ADDRESS_BACKUP };
    esp_err_t err = ESP_FAIL;
    for (int i = 0; i < 2 && err != ESP_OK; i++) {
        esp_lcd_panel_io_i2c_config_t io_cfg = {
            .dev_addr = addrs[i],
            .control_phase_bytes = 1,
            .dc_bit_offset = 0,
            .lcd_cmd_bits = 16,
            .flags = { .disable_control_phase = 1 },
            .scl_speed_hz = 400000,
        };
        esp_lcd_panel_io_handle_t io = NULL;
        tp_cfg.driver_data = &io_cfg.dev_addr;
        err = esp_lcd_new_panel_io_i2c(s_i2c, &io_cfg, &io);
        if (err == ESP_OK) err = esp_lcd_touch_new_i2c_gt911(io, &tp_cfg, &s_touch);
        if (err != ESP_OK) ESP_LOGW(TAG, "GT911 an Adresse 0x%02x nicht erreichbar", addrs[i]);
    }
    return err;
}

static esp_err_t backlight_init(void)
{
    const ledc_timer_config_t t = {
        .clk_cfg = LEDC_USE_PLL_DIV_CLK,
        .duty_resolution = LEDC_TIMER_11_BIT,
        .freq_hz = BL_PWM_HZ,
        .speed_mode = LEDC_LOW_SPEED_MODE,
        .timer_num = LEDC_TIMER_0,
    };
    ESP_RETURN_ON_ERROR(ledc_timer_config(&t), TAG, "LEDC timer");
    const ledc_channel_config_t c = {
        .gpio_num = PIN_BACKLIGHT,
        .speed_mode = LEDC_LOW_SPEED_MODE,
        .channel = LEDC_CHANNEL_0,
        .intr_type = LEDC_INTR_DISABLE,
        .timer_sel = LEDC_TIMER_0,
        .duty = 0,
        .hpoint = 0,
    };
    return ledc_channel_config(&c);
}

esp_err_t board_set_backlight(int percent)
{
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;
    // Kennlinie wie im Elecrow-Beispiel: Duty 200 + 18 pro Prozent (11 Bit)
    uint32_t duty = percent ? (uint32_t)(percent * 18 + 200) : 0;
    ESP_RETURN_ON_ERROR(ledc_set_duty(LEDC_LOW_SPEED_MODE, LEDC_CHANNEL_0, duty), TAG, "duty");
    return ledc_update_duty(LEDC_LOW_SPEED_MODE, LEDC_CHANNEL_0);
}

static esp_err_t panel_init(void)
{
    esp_lcd_dsi_bus_config_t bus = {
        .bus_id = 0,
        .num_data_lanes = 2,
        .phy_clk_src = MIPI_DSI_PHY_CLK_SRC_DEFAULT,
        .lane_bit_rate_mbps = 900,
    };
    ESP_RETURN_ON_ERROR(esp_lcd_new_dsi_bus(&bus, &s_dsi_bus), TAG, "DSI bus");

    esp_lcd_dbi_io_config_t dbi = { .virtual_channel = 0, .lcd_cmd_bits = 8, .lcd_param_bits = 8 };
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_io_dbi(s_dsi_bus, &dbi, &s_dbi_io), TAG, "DBI io");

    const esp_lcd_dpi_panel_config_t dpi = {
        .dpi_clk_src = MIPI_DSI_DPI_CLK_SRC_DEFAULT,
        .dpi_clock_freq_mhz = 51,
        .virtual_channel = 0,
        .pixel_format = LCD_COLOR_PIXEL_FORMAT_RGB565,
        .num_fbs = 1,
        .video_timing = {
            .h_size = OB_LCD_H,
            .v_size = OB_LCD_V,
            .hsync_back_porch = 160,
            .hsync_pulse_width = 70,
            .hsync_front_porch = 160,
            .vsync_back_porch = 23,
            .vsync_pulse_width = 10,
            .vsync_front_porch = 12,
        },
        .flags.use_dma2d = true,
    };
    ek79007_vendor_config_t vendor = { .mipi_config = { .dsi_bus = s_dsi_bus, .dpi_config = &dpi } };
    const esp_lcd_panel_dev_config_t pc = {
        .reset_gpio_num = -1,
        .rgb_ele_order = LCD_RGB_ELEMENT_ORDER_RGB,
        .bits_per_pixel = BITS_PER_PIXEL,
        .vendor_config = &vendor,
    };
    ESP_RETURN_ON_ERROR(esp_lcd_new_panel_ek79007(s_dbi_io, &pc, &s_panel), TAG, "EK79007");
    ESP_RETURN_ON_ERROR(esp_lcd_panel_reset(s_panel), TAG, "panel reset");
    return esp_lcd_panel_init(s_panel);
}

static esp_err_t lvgl_start(void)
{
    const lvgl_port_cfg_t cfg = {
        .task_priority = configMAX_PRIORITIES - 4,
        .task_stack = 16384,
        .task_affinity = -1,
        .task_max_sleep_ms = 10,
        .timer_period_ms = 5,
    };
    ESP_RETURN_ON_ERROR(lvgl_port_init(&cfg), TAG, "lvgl_port_init");

    const lvgl_port_display_cfg_t disp = {
        .io_handle = s_dbi_io,
        .panel_handle = s_panel,
        .control_handle = s_panel,
        .buffer_size = OB_LCD_H * OB_LCD_V,   // in Pixeln
        .double_buffer = true,
        .hres = OB_LCD_H,
        .vres = OB_LCD_V,
        .monochrome = false,
        .color_format = LV_COLOR_FORMAT_RGB565,
        .rotation = { .swap_xy = false, .mirror_x = false, .mirror_y = false },
        .flags = { .buff_dma = false, .buff_spiram = true, .sw_rotate = true, .swap_bytes = false,  // true ergibt falsche Farben (Weiss -> Gruen, Gruen -> Lila), im Foto bestaetigt 2026-11-15
                   .full_refresh = false, .direct_mode = false },
    };
    const lvgl_port_display_dsi_cfg_t dsi = { .flags = { .avoid_tearing = false } };
    s_disp = lvgl_port_add_disp_dsi(&disp, &dsi);
    ESP_RETURN_ON_FALSE(s_disp, ESP_FAIL, TAG, "lvgl_port_add_disp_dsi");

    const lvgl_port_touch_cfg_t touch = { .disp = s_disp, .handle = s_touch };
    lv_indev_t *indev = lvgl_port_add_touch(&touch);
    ESP_RETURN_ON_FALSE(indev, ESP_FAIL, TAG, "lvgl_port_add_touch");

    // Schnelleres Ansprechen: Beruehrung alle 10 ms abfragen (Standard 33 ms) und das Bild alle 16 ms auffrischen (Standard 33 ms)
    lvgl_port_lock(0);
    lv_timer_t *rt = lv_indev_get_read_timer(indev);
    if (rt) lv_timer_set_period(rt, 10);
    lv_timer_t *dt = lv_display_get_refr_timer(s_disp);
    if (dt) lv_timer_set_period(dt, 16);
    lvgl_port_unlock();
    return ESP_OK;
}

esp_err_t board_init(void)
{
    ESP_RETURN_ON_ERROR(power_init(), TAG, "Spannungen");
    ESP_RETURN_ON_ERROR(backlight_init(), TAG, "Backlight");
    board_set_backlight(0);
    ESP_RETURN_ON_ERROR(i2c_init(), TAG, "I2C");
    ESP_RETURN_ON_ERROR(touch_init(), TAG, "Touch");
    ESP_RETURN_ON_ERROR(panel_init(), TAG, "Panel");
    ESP_RETURN_ON_ERROR(lvgl_start(), TAG, "LVGL");
    ESP_LOGI(TAG, "Display bereit (%dx%d)", OB_LCD_H, OB_LCD_V);
    return ESP_OK;
}
