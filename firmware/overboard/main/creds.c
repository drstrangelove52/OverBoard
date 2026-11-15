#include "creds.h"

#include "nvs.h"

#define NS "ob_cred"

bool creds_get(const char *key, char *out, size_t n)
{
    nvs_handle_t h;
    if (nvs_open(NS, NVS_READONLY, &h) != ESP_OK) return false;
    size_t len = n;
    bool ok = nvs_get_str(h, key, out, &len) == ESP_OK && out[0];
    nvs_close(h);
    return ok;
}

esp_err_t creds_put(const char *key, const char *val)
{
    nvs_handle_t h;
    esp_err_t r = nvs_open(NS, NVS_READWRITE, &h);
    if (r != ESP_OK) return r;
    r = nvs_set_str(h, key, val);
    if (r == ESP_OK) r = nvs_commit(h);
    nvs_close(h);
    return r;
}
