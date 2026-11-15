idf_component_register(SRCS "main.c" "board.c" "net.c" "ui.c" "audio.c" "notify.c" "fonts.c"
                            "pclink.c" "config.c" "creds.c" "theme.c" "i18n.c" "mdi.c" "mdi_table.c"
                       INCLUDE_DIRS "."
                       EMBED_FILES "fonts/Montserrat-Medium.ttf" "fonts/materialdesignicons-webfont.ttf"
                       PRIV_REQUIRES esp_lcd esp_driver_gpio esp_driver_ledc esp_driver_i2c esp_driver_i2s
                                     esp_hw_support esp_netif esp_wifi esp_event nvs_flash json esp_timer mqtt esp_partition mbedtls esp_app_format)
