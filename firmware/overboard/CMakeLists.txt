cmake_minimum_required(VERSION 3.16)
# sdkconfig.secrets (nicht in Git) enthaelt HA-Zugangsdaten
set(SDKCONFIG_DEFAULTS "sdkconfig.defaults;sdkconfig.secrets")
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(overboard)
