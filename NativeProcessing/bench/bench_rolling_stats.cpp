// Native throughput micro-benchmark for tl_push_batch. Prints one line per window size.
#include <chrono>
#include <cstdio>
#include <random>
#include <vector>

#include "templab/rolling_stats.h"

int main() {
    const int channels = 4;
    const int batch = 4096;
    const int iterations = 2000;
    std::vector<int32_t> ch(batch);
    std::vector<double> vals(batch);
    std::vector<tl_stats> out(batch);
    std::mt19937_64 rng(1);
    std::normal_distribution<double> nd(25.0, 1.0);
    for (int i = 0; i < batch; ++i) {
        ch[i] = i % channels;
        vals[i] = nd(rng);
    }
    std::printf("window,samples,seconds,samples_per_second\n");
    for (int window : {1, 20, 200, 2000}) {
        tl_processor* p = nullptr;
        tl_create(channels, window, &p);
        auto t0 = std::chrono::steady_clock::now();
        for (int it = 0; it < iterations; ++it) {
            tl_push_batch(p, ch.data(), vals.data(), batch, out.data(), batch, nullptr);
        }
        auto t1 = std::chrono::steady_clock::now();
        double secs = std::chrono::duration<double>(t1 - t0).count();
        double n = static_cast<double>(batch) * iterations;
        std::printf("%d,%.0f,%.4f,%.0f\n", window, n, secs, n / secs);
        tl_destroy(p);
    }
    return 0;
}
