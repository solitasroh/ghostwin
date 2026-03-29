/// @file conpty_benchmark.cpp
/// ConPTY + VtCore performance benchmarks (B1~B3).

#include "conpty_session.h"
#include "vt_core.h"

#include <cstdio>
#include <cstring>
#include <algorithm>
#include <vector>
#include <chrono>
#include <thread>

using namespace ghostwin;
using Clock = std::chrono::high_resolution_clock;

static bool send_str(ConPtySession& s, const char* str) {
    return s.send_input({reinterpret_cast<const uint8_t*>(str), strlen(str)});
}

// B1: I/O throughput -- ReadFile -> VtCore.write() pipeline
int benchmark_io_throughput() {
    fprintf(stderr,"[B1] I/O Throughput\n");

    // Use cmd /c with a for loop so the child exits when done
    // 500 lines x 200 chars = ~100KB (enough for throughput measurement)
    SessionConfig config;
    config.shell_path = L"cmd.exe /c \"for /L %i in (1,1,500) do @echo AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"";
    auto start = Clock::now();
    auto session = ConPtySession::create(config);
    if (!session) {
        fprintf(stderr,"  [FAIL] session creation failed\n");
        return 1;
    }

    // Wait for child process to exit (for loop completion)
    while (session->is_alive()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        // Safety timeout: 30 seconds
        auto elapsed = Clock::now() - start;
        if (std::chrono::duration_cast<std::chrono::seconds>(elapsed).count() > 30) {
            fprintf(stderr,"  [WARN] timeout after 30s\n");
            break;
        }
    }

    auto elapsed = Clock::now() - start;
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();

    // 500 lines x 200 chars = ~100KB
    double mb = (500.0 * 200) / (1024.0 * 1024.0);
    double throughput = (ms > 0) ? mb / (ms / 1000.0) : 0;

    fprintf(stderr,"  Data: ~%.1f MB, Time: %lldms, Throughput: %.1f MB/s\n", mb, ms, throughput);
    fprintf(stderr,"  Target: >= 100 MB/s -- %s\n", throughput >= 100.0 ? "PASS" : "BELOW TARGET");
    return 0;
}

// B2: VT parse latency -- VtCore.write() per-call timing
int benchmark_vt_parse_latency() {
    fprintf(stderr,"[B2] VT Parse Latency\n");

    // Direct VtCore benchmark (no ConPTY overhead)
    auto vt = VtCore::create(80, 24);
    if (!vt) {
        fprintf(stderr,"  [FAIL] VtCore::create failed\n");
        return 1;
    }

    // Prepare 4KB buffer with mixed VT content (small for Debug builds)
    static constexpr size_t BUF_SIZE = 4096;
    auto buf = std::make_unique<uint8_t[]>(BUF_SIZE);

    // Fill with realistic VT data: colored text + cursor moves
    const char* pattern = "\x1b[31mHello\x1b[0m World \x1b[2J\x1b[H";
    size_t pat_len = strlen(pattern);
    for (size_t i = 0; i < BUF_SIZE; i++) {
        buf[i] = static_cast<uint8_t>(pattern[i % pat_len]);
    }

    static constexpr int ITERATIONS = 100;
    std::vector<double> timings;
    timings.reserve(ITERATIONS);

    for (int i = 0; i < ITERATIONS; i++) {
        auto t0 = Clock::now();
        vt->write({buf.get(), BUF_SIZE});
        auto t1 = Clock::now();

        double us = std::chrono::duration_cast<std::chrono::nanoseconds>(t1 - t0).count() / 1000.0;
        timings.push_back(us);
    }

    // Calculate stats
    std::sort(timings.begin(), timings.end());
    double sum = 0;
    for (auto t : timings) sum += t;
    double avg = sum / ITERATIONS;
    double p50 = timings[ITERATIONS / 2];
    double p99 = timings[static_cast<size_t>(ITERATIONS * 0.99)];
    double max_val = timings.back();

    fprintf(stderr,"  Iterations: %d x %zuB\n", ITERATIONS, BUF_SIZE);
    fprintf(stderr,"  Avg: %.0f us, P50: %.0f us, P99: %.0f us, Max: %.0f us\n",
           avg, p50, p99, max_val);
    fprintf(stderr,"  Target: avg < 1000us, P99 < 5000us -- %s\n",
           (avg < 1000.0 && p99 < 5000.0) ? "PASS" : "BELOW TARGET");
    return 0;
}

// B3: Shutdown time -- destructor completion
int benchmark_shutdown_time() {
    fprintf(stderr,"[B3] Shutdown Time\n");

    static constexpr int RUNS = 5;
    std::vector<long long> times;

    for (int i = 0; i < RUNS; i++) {
        auto start = Clock::now();
        {
            SessionConfig config;
            config.shell_path = L"cmd.exe";
            auto session = ConPtySession::create(config);
            if (!session) {
                fprintf(stderr,"  [FAIL] session creation failed\n");
                return 1;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(300));
            // destructor runs here
        }
        auto elapsed = Clock::now() - start;
        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();
        times.push_back(ms);
    }

    std::sort(times.begin(), times.end());
    long long avg = 0;
    for (auto t : times) avg += t;
    avg /= RUNS;

    fprintf(stderr,"  Runs: %d, Avg: %lldms, Min: %lldms, Max: %lldms\n",
           RUNS, avg, times.front(), times.back());
    fprintf(stderr,"  Target: < 2000ms (goal), < 5000ms (max) -- %s\n",
           times.back() < 5000 ? "PASS" : "BELOW TARGET");
    return 0;
}

int main() {
    fprintf(stderr,"=== ConPTY Performance Benchmarks ===\n\n");

    benchmark_io_throughput();
    fprintf(stderr,"\n");
    benchmark_vt_parse_latency();
    fprintf(stderr,"\n");
    benchmark_shutdown_time();

    fprintf(stderr,"\n=== Benchmarks Complete ===\n");
    return 0;
}
