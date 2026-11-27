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
option(SENTIX_ENABLE_ONNX "Enable ONNX Runtime C++ inference engine" ON)

# Pinned official ONNX Runtime release (specs/002 §7), used only when no installed onnxruntime package is found.
set(SENTIX_ONNXRUNTIME_VERSION "1.20.1" CACHE STRING "ONNX Runtime release fetched when not installed")
set(SENTIX_ONNXRUNTIME_SHA256 "9c32108e9a1c78b090a95c2a219f244dd5f067d4caf68fb0f967103f3f20839c" CACHE STRING
  "SHA-256 of microsoft.ml.onnxruntime.<SENTIX_ONNXRUNTIME_VERSION>.nupkg (mandatory when the archive is fetched)")

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

# ONNX Runtime (specs/002 §7). Only the C/C++ headers are needed at build time: sentix-native dlopen()s
# libonnxruntime.so.1 on demand (next to the executable first, then via the dynamic loader search path), so the
# daemon has no hard runtime dependency and fails closed (DEGRADED / MODEL_NOT_READY) when the library is absent.
set(SENTIX_HAS_ONNX 0)
if(SENTIX_ENABLE_ONNX)
  add_library(sentix_onnxruntime_headers INTERFACE)

  find_package(onnxruntime CONFIG QUIET)
  if(onnxruntime_FOUND AND TARGET onnxruntime::onnxruntime)
    message(STATUS "Using installed onnxruntime ${onnxruntime_VERSION}")
    get_target_property(_sentix_ort_includes onnxruntime::onnxruntime INTERFACE_INCLUDE_DIRECTORIES)
    target_include_directories(sentix_onnxruntime_headers SYSTEM INTERFACE ${_sentix_ort_includes})
  else()
    set(_sentix_ort_archive "microsoft.ml.onnxruntime-${SENTIX_ONNXRUNTIME_VERSION}.zip")
    set(_sentix_ort_url
      "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/${SENTIX_ONNXRUNTIME_VERSION}/microsoft.ml.onnxruntime.${SENTIX_ONNXRUNTIME_VERSION}.nupkg")

    if(SENTIX_ONNXRUNTIME_SHA256 STREQUAL "")
      # Refuse to build against an unverified archive. Report the observed digest so it can be checked and pinned.
      set(_sentix_ort_probe "${CMAKE_BINARY_DIR}/_deps/${_sentix_ort_archive}.unverified")
      file(DOWNLOAD "${_sentix_ort_url}" "${_sentix_ort_probe}" STATUS _sentix_ort_status TLS_VERIFY ON)
      list(GET _sentix_ort_status 0 _sentix_ort_code)
      if(_sentix_ort_code EQUAL 0)
        file(SHA256 "${_sentix_ort_probe}" _sentix_ort_observed)
        set(_sentix_ort_hint "Observed SHA-256 of ${_sentix_ort_url}:\n  ${_sentix_ort_observed}\n"
                             "Verify it against the official release, then pin it below.")
      else()
        set(_sentix_ort_hint "Probe download failed: ${_sentix_ort_status}")
      endif()
      file(REMOVE "${_sentix_ort_probe}")
      message(FATAL_ERROR
        "SENTIX_ENABLE_ONNX=ON, no installed onnxruntime package was found, and SENTIX_ONNXRUNTIME_SHA256 is not pinned.\n"
        ${_sentix_ort_hint}
        "\nPin it in src/native/CMakeLists.txt (SENTIX_ONNXRUNTIME_SHA256) or pass -DSENTIX_ONNXRUNTIME_SHA256=<hash>; "
        "alternatively install onnxruntime or configure with -DSENTIX_ENABLE_ONNX=OFF.")
    endif()

    message(STATUS "onnxruntime not found on system - fetching pinned ${_sentix_ort_archive} via FetchContent")
    FetchContent_Declare(onnxruntime_prebuilt
      URL "${_sentix_ort_url}"
      URL_HASH SHA256=${SENTIX_ONNXRUNTIME_SHA256}
      DOWNLOAD_NAME "${_sentix_ort_archive}"
      DOWNLOAD_EXTRACT_TIMESTAMP TRUE
      SOURCE_SUBDIR _sentix_prebuilt_no_cmake)  # prebuilt archive: never add_subdirectory()
    FetchContent_MakeAvailable(onnxruntime_prebuilt)

    set(_sentix_ort_root "${onnxruntime_prebuilt_SOURCE_DIR}")
    if(EXISTS "${_sentix_ort_root}/build/native/include/onnxruntime_cxx_api.h")
      set(_sentix_ort_include_dir "${_sentix_ort_root}/build/native/include")
    elseif(EXISTS "${_sentix_ort_root}/include/onnxruntime_cxx_api.h")
      set(_sentix_ort_include_dir "${_sentix_ort_root}/include")
    else()
      message(FATAL_ERROR "Fetched ONNX Runtime archive lacks onnxruntime_cxx_api.h (${_sentix_ort_root})")
    endif()
    target_include_directories(sentix_onnxruntime_headers SYSTEM INTERFACE "${_sentix_ort_include_dir}")

    # Stage the shared library next to sentix-native and the test binary (found via the /proc/self/exe directory).
    file(GLOB _sentix_ort_libs
      "${_sentix_ort_root}/runtimes/linux-x64/native/libonnxruntime*.so*"
      "${_sentix_ort_root}/lib/libonnxruntime*.so*")
    if(NOT _sentix_ort_libs)
      message(FATAL_ERROR "Fetched ONNX Runtime archive lacks libonnxruntime*.so* (${_sentix_ort_root})")
    endif()
    file(COPY ${_sentix_ort_libs} DESTINATION "${CMAKE_BINARY_DIR}" FOLLOW_SYMLINK_CHAIN)
  endif()

  set(SENTIX_HAS_ONNX 1)
endif()
message(STATUS "sentix-native ONNX-FinBERT engine: SENTIX_HAS_ONNX=${SENTIX_HAS_ONNX}")

# ---------------------------------------------------------------- warnings
add_library(sentix_warnings INTERFACE)
target_compile_options(sentix_warnings INTERFACE
  -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion -Wnon-virtual-dtor
  $<$<BOOL:${SENTIX_WERROR}>:-Werror>)

# ---------------------------------------------------------------- core library
add_library(sentix_core STATIC
  src/config.cpp
  src/dedup_cache.cpp
  src/engine_config.cpp
  src/engine_factory.cpp
  src/finbert_postprocess.cpp
  src/framing.cpp
  src/hashing.cpp
  src/protocol.cpp
  src/scoring_pipeline.cpp
  src/sentiment_engine.cpp
  src/text_cleaner.cpp
  src/unix_socket_server.cpp
  src/watchlist_matcher.cpp
  src/wordpiece_tokenizer.cpp)

target_include_directories(sentix_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_definitions(sentix_core PUBLIC
  SENTIX_ENGINE_VERSION="${PROJECT_VERSION}"
  SENTIX_HAS_ONNX=${SENTIX_HAS_ONNX})
target_link_libraries(sentix_core
  PUBLIC nlohmann_json::nlohmann_json Threads::Threads
  PRIVATE OpenSSL::Crypto sentix_warnings)
if(SENTIX_HAS_ONNX)
  target_sources(sentix_core PRIVATE src/onnx_finbert_engine.cpp)
  target_link_libraries(sentix_core PRIVATE sentix_onnxruntime_headers ${CMAKE_DL_LIBS})
endif()

# ---------------------------------------------------------------- daemon (build/native/sentix-native)
add_executable(sentix-native src/main.cpp)
target_link_libraries(sentix-native PRIVATE sentix_core sentix_warnings)
set_target_properties(sentix-native PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR})
if(SENTIX_HAS_ONNX AND TARGET onnxruntime::onnxruntime)
  # Installed package: stage the runtime next to the binaries as well (dlopen search starts in the executable dir).
  add_custom_command(TARGET sentix-native POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:onnxruntime::onnxruntime> ${CMAKE_BINARY_DIR}/libonnxruntime.so.1
    VERBATIM)
endif()

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
    tests/test_engine_config.cpp
    tests/test_engine_factory.cpp
    tests/test_finbert_postprocess.cpp
    tests/test_framing.cpp
    tests/test_hashing.cpp
    tests/test_protocol.cpp
    tests/test_scoring_pipeline.cpp
    tests/test_sentiment_engine.cpp
    tests/test_text_cleaner.cpp
    tests/test_unix_socket_server.cpp
    tests/test_watchlist_matcher.cpp
    tests/test_wordpiece_tokenizer.cpp)
  if(SENTIX_HAS_ONNX)
    target_sources(sentix_native_tests PRIVATE tests/test_onnx_finbert_engine.cpp)
  endif()
  set_target_properties(sentix_native_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR})
  target_link_libraries(sentix_native_tests PRIVATE sentix_core sentix_warnings GTest::gtest GTest::gtest_main)
  target_include_directories(sentix_native_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/tests)
  target_compile_definitions(sentix_native_tests PRIVATE
    SENTIX_TEST_UNIVERSE_JSON="${CMAKE_CURRENT_SOURCE_DIR}/../../config/universe.json"
    SENTIX_NATIVE_BINARY="$<TARGET_FILE:sentix-native>")
  add_dependencies(sentix_native_tests sentix-native)

  include(GoogleTest)
  gtest_discover_tests(sentix_native_tests DISCOVERY_MODE PRE_TEST)
endif()
