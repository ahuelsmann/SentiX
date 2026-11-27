cmake_minimum_required(VERSION 3.25)

project(sentix_native
  VERSION 0.1.0
  DESCRIPTION "SentiX C++20 fast-path daemon (specs/002-ipc-contract.md)"
  LANGUAGES CXX)

if(NOT CMAKE_SYSTEM_NAME STREQUAL "Linux")
  message(FATAL_ERROR "sentix-native targets headless Linux only (ADR-01). Detected: ${CMAKE_SYSTEM_NAME}")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

option(SENTIX_BUILD_TESTS "Build the GoogleTest suite" ON)
option(SENTIX_WERROR "Treat compiler warnings as errors" OFF)

include(FetchContent)

# ---------------------------------------------------------------- dependencies
find_package(Threads REQUIRED)
find_package(OpenSSL REQUIRED)

find_package(nlohmann_json 3.11 CONFIG QUIET)
if(NOT nlohmann_json_FOUND)
  message(STATUS "nlohmann_json not found on system - fetching v3.11.3 via FetchContent")
  FetchContent_Declare(nlohmann_json
    URL https://github.com/nlohmann/json/releases/download/v3.11.3/json.tar.xz
    URL_HASH SHA256=d6c65aca6b1ed68e7a182f4757257b107ae403032760ed6ef121c9d55e81757d
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    SYSTEM)
  set(JSON_BuildTests OFF CACHE INTERNAL "")
  set(JSON_Install OFF CACHE INTERNAL "")
  FetchContent_MakeAvailable(nlohmann_json)
endif()

# ---------------------------------------------------------------- warnings
add_library(sentix_warnings INTERFACE)
target_compile_options(sentix_warnings INTERFACE
  -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion -Wnon-virtual-dtor
  $<$<BOOL:${SENTIX_WERROR}>:-Werror>)

# ---------------------------------------------------------------- core library
add_library(sentix_core STATIC
  src/config.cpp
  src/dedup_cache.cpp
  src/framing.cpp
  src/hashing.cpp
  src/protocol.cpp
  src/scoring_pipeline.cpp
  src/sentiment_engine.cpp
  src/text_cleaner.cpp
  src/unix_socket_server.cpp
  src/watchlist_matcher.cpp)

target_include_directories(sentix_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_definitions(sentix_core PUBLIC SENTIX_ENGINE_VERSION="${PROJECT_VERSION}")
target_link_libraries(sentix_core
  PUBLIC nlohmann_json::nlohmann_json Threads::Threads
  PRIVATE OpenSSL::Crypto sentix_warnings)

# ---------------------------------------------------------------- daemon (build/native/sentix-native)
add_executable(sentix-native src/main.cpp)
target_link_libraries(sentix-native PRIVATE sentix_core sentix_warnings)
set_target_properties(sentix-native PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR})

# ---------------------------------------------------------------- tests
if(SENTIX_BUILD_TESTS)
  enable_testing()

  find_package(GTest CONFIG QUIET)
  if(NOT GTest_FOUND)
    message(STATUS "GTest not found on system - fetching googletest v1.15.2 via FetchContent")
    FetchContent_Declare(googletest
      URL https://github.com/google/googletest/archive/refs/tags/v1.15.2.tar.gz
      URL_HASH SHA256=7b42b4d6ed48810c5362c265a17faebe90dc2373c885e5216439d37927f02926
      DOWNLOAD_EXTRACT_TIMESTAMP TRUE
      SYSTEM)
    set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
    set(BUILD_GMOCK OFF CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(googletest)
  endif()

  add_executable(sentix_native_tests
    tests/test_config.cpp
    tests/test_dedup_cache.cpp
    tests/test_framing.cpp
    tests/test_hashing.cpp
    tests/test_protocol.cpp
    tests/test_scoring_pipeline.cpp
    tests/test_sentiment_engine.cpp
    tests/test_text_cleaner.cpp
    tests/test_unix_socket_server.cpp
    tests/test_watchlist_matcher.cpp)
  target_link_libraries(sentix_native_tests PRIVATE sentix_core sentix_warnings GTest::gtest GTest::gtest_main)
  target_include_directories(sentix_native_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/tests)
  target_compile_definitions(sentix_native_tests PRIVATE
    SENTIX_TEST_UNIVERSE_JSON="${CMAKE_CURRENT_SOURCE_DIR}/../../config/universe.json"
    SENTIX_NATIVE_BINARY="$<TARGET_FILE:sentix-native>")
  add_dependencies(sentix_native_tests sentix-native)

  include(GoogleTest)
  gtest_discover_tests(sentix_native_tests DISCOVERY_MODE PRE_TEST)
endif()
