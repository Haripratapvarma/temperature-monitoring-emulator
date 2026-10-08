// Native tests: exercise the C ABI only (what the managed side sees) plus a naive reference.
// Dependency-free so they build anywhere CMake does.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <deque>
#include <functional>
#include <limits>
#include <random>
#include <string>
#include <vector>

#include "templab/rolling_stats.h"

namespace {

int g_failures = 0;
int g_checks = 0;

#define CHECK(cond)                                                                       \
    do {                                                                                  \
        ++g_checks;                                                                       \
        if (!(cond)) {                                                                    \
            ++g_failures;                                                                 \
            std::printf("  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);                 \
        }                                                                                 \
    } while (0)

#define CHECK_NEAR(a, b, tol)                                                             \
    do {                                                                                  \
        ++g_checks;                                                                       \
        double _a = (a), _b = (b);                                                        \
        if (!(std::fabs(_a - _b) <= (tol) * std::max(1.0, std::fabs(_b)))) {              \
            ++g_failures;                                                                 \
            std::printf("  FAIL %s:%d: %s=%.17g vs %s=%.17g\n", __FILE__, __LINE__, #a, _a, #b, _b); \
        }                                                                                 \
    } while (0)

// Naive O(W) reference: keep the last W valid values and recompute everything.
struct Reference {
    std::size_t w;
    std::deque<double> vals;
    explicit Reference(std::size_t window) : w(window) {}
    void push(double v) {
        vals.push_back(v);
        if (vals.size() > w) vals.pop_front();
    }
    double mean() const {
        double s = 0;
        for (double v : vals) s += v;
        return s / static_cast<double>(vals.size());
    }
    double min() const { return *std::min_element(vals.begin(), vals.end()); }
    double max() const { return *std::max_element(vals.begin(), vals.end()); }
};

struct Proc {
    tl_processor* p = nullptr;
    Proc(int ch, int w) { CHECK(tl_create(ch, w, &p) == TL_OK); }
    ~Proc() { if (p) tl_destroy(p); }
};

void test_known_sequence() {
    Proc pr(1, 3);
    tl_stats s{};
    CHECK(tl_push(pr.p, 0, 1.0, &s) == TL_OK);
    CHECK(s.count == 1 && s.is_warm == 0);
    CHECK_NEAR(s.mean, 1.0, 1e-15);
    tl_push(pr.p, 0, 2.0, &s);
    tl_push(pr.p, 0, 6.0, &s);
    CHECK(s.count == 3 && s.is_warm == 1);
    CHECK_NEAR(s.mean, 3.0, 1e-15);
    CHECK(s.min == 1.0 && s.max == 6.0);
    tl_push(pr.p, 0, 4.0, &s);  // window = {2,6,4}
    CHECK_NEAR(s.mean, 4.0, 1e-15);
    CHECK(s.min == 2.0 && s.max == 6.0);
    tl_push(pr.p, 0, 0.0, &s);  // {6,4,0}
    CHECK(s.min == 0.0 && s.max == 6.0);
    tl_push(pr.p, 0, 5.0, &s);  // {4,0,5}
    CHECK(s.min == 0.0 && s.max == 5.0);
    CHECK_NEAR(s.mean, 3.0, 1e-15);
}

void test_window_size_one() {
    Proc pr(2, 1);
    tl_stats s{};
    for (double v : {3.5, -1.0, 7.25, 7.25, 0.0}) {
        CHECK(tl_push(pr.p, 1, v, &s) == TL_OK);
        CHECK(s.count == 1 && s.is_warm == 1);
        CHECK(s.mean == v && s.min == v && s.max == v);
    }
    // Channel 0 untouched.
    CHECK(tl_get_stats(pr.p, 0, &s) == TL_OK);
    CHECK(s.count == 0 && std::isnan(s.mean));
}

void test_warm_up() {
    Proc pr(1, 5);
    tl_stats s{};
    for (int i = 1; i <= 7; ++i) {
        tl_push(pr.p, 0, static_cast<double>(i), &s);
        CHECK(s.count == std::min(i, 5));
        CHECK(s.is_warm == (i >= 5 ? 1 : 0));
    }
}

void test_reset() {
    Proc pr(3, 4);
    tl_stats s{};
    for (int c = 0; c < 3; ++c)
        for (int i = 0; i < 6; ++i) tl_push(pr.p, c, 10.0 + i, nullptr);
    CHECK(tl_reset(pr.p, 1) == TL_OK);
    tl_get_stats(pr.p, 1, &s);
    CHECK(s.count == 0);
    tl_get_stats(pr.p, 0, &s);
    CHECK(s.count == 4);
    tl_push(pr.p, 1, 99.0, &s);
    CHECK(s.count == 1 && s.mean == 99.0 && s.min == 99.0 && s.max == 99.0);
    CHECK(tl_reset(pr.p, -1) == TL_OK);
    for (int c = 0; c < 3; ++c) {
        tl_get_stats(pr.p, c, &s);
        CHECK(s.count == 0);
    }
    CHECK(tl_reset(pr.p, 3) == TL_ERR_CHANNEL_OUT_OF_RANGE);
}

void test_invalid_values() {
    Proc pr(1, 3);
    tl_stats s{};
    tl_push(pr.p, 0, 20.0, &s);
    const double bad[] = {std::numeric_limits<double>::quiet_NaN(), std::numeric_limits<double>::infinity(),
                          -std::numeric_limits<double>::infinity(), -300.0, 2500.0};
    for (double b : bad) {
        CHECK(tl_push(pr.p, 0, b, &s) == TL_INVALID_VALUE);
        CHECK(s.last_status == TL_INVALID_VALUE);
        CHECK(s.count == 1 && s.mean == 20.0);  // previous valid stats retained
    }
    CHECK(tl_push(pr.p, 0, TL_MIN_VALID_C, &s) == TL_OK);
    CHECK(tl_push(pr.p, 0, TL_MAX_VALID_C, &s) == TL_OK);
    CHECK(s.count == 3);
}

void test_against_reference_random() {
    std::mt19937_64 rng(12345);
    for (int window : {1, 2, 3, 7, 20, 64, 257}) {
        Proc pr(4, window);
        std::vector<Reference> refs(4, Reference(static_cast<std::size_t>(window)));
        std::normal_distribution<double> nd(25.0, 5.0);
        std::uniform_int_distribution<int> chd(0, 3);
        std::uniform_int_distribution<int> bad(0, 50);
        int mismatches = 0;
        for (int i = 0; i < 20000; ++i) {
            int ch = chd(rng);
            double v = bad(rng) == 0 ? std::numeric_limits<double>::quiet_NaN() : nd(rng);
            tl_stats s{};
            tl_status st = tl_push(pr.p, ch, v, &s);
            if (st == TL_OK) refs[ch].push(v);
            auto& r = refs[ch];
            if (r.vals.empty()) continue;
            bool ok = s.count == static_cast<int32_t>(r.vals.size()) && s.min == r.min() && s.max == r.max() &&
                      std::fabs(s.mean - r.mean()) <= 1e-9 * std::max(1.0, std::fabs(r.mean()));
            if (!ok) ++mismatches;
        }
        CHECK(mismatches == 0);
        if (mismatches) std::printf("  window %d: %d mismatches\n", window, mismatches);
    }
}

void test_drift_long_run() {
    // Large offset + small variation is the worst case for a running sum.
    Proc pr(1, 50);
    Reference ref(50);
    tl_stats s{};
    std::mt19937_64 rng(7);
    std::uniform_real_distribution<double> u(-0.01, 0.01);
    for (int i = 0; i < 2000000; ++i) {
        double v = 1999.0 + u(rng);
        tl_push(pr.p, 0, v, &s);
        ref.push(v);
    }
    CHECK_NEAR(s.mean, ref.mean(), 1e-12);
}

void test_batch() {
    Proc pr(2, 2);
    const int32_t chs[] = {0, 1, 0, 1, 0};
    const double vals[] = {1.0, 10.0, std::numeric_limits<double>::quiet_NaN(), 20.0, 3.0};
    tl_stats out[5];
    int32_t invalid = -1;
    CHECK(tl_push_batch(pr.p, chs, vals, 5, out, 5, &invalid) == TL_OK);
    CHECK(invalid == 1);
    CHECK(out[2].last_status == TL_INVALID_VALUE && out[2].count == 1);
    CHECK(out[3].mean == 15.0);
    CHECK(out[4].mean == 2.0 && out[4].is_warm == 1);
    CHECK(tl_push_batch(pr.p, chs, vals, 5, out, 4, &invalid) == TL_ERR_BUFFER_TOO_SMALL);
    const int32_t badch[] = {0, 5};
    tl_stats before{};
    tl_get_stats(pr.p, 0, &before);
    CHECK(tl_push_batch(pr.p, badch, vals, 2, nullptr, 0, nullptr) == TL_ERR_CHANNEL_OUT_OF_RANGE);
    tl_stats after{};
    tl_get_stats(pr.p, 0, &after);
    CHECK(before.mean == after.mean && before.count == after.count);  // bad batch changes nothing
    CHECK(tl_push_batch(pr.p, nullptr, nullptr, 0, nullptr, 0, nullptr) == TL_OK);
    CHECK(tl_push_batch(pr.p, nullptr, vals, 1, nullptr, 0, nullptr) == TL_ERR_NULL_ARGUMENT);
}

void test_argument_errors() {
    tl_processor* p = reinterpret_cast<tl_processor*>(0x1);
    CHECK(tl_create(0, 5, &p) == TL_ERR_INVALID_ARGUMENT);
    CHECK(p == nullptr);
    CHECK(tl_create(TL_MAX_CHANNELS + 1, 5, &p) == TL_ERR_INVALID_ARGUMENT);
    CHECK(tl_create(4, 0, &p) == TL_ERR_INVALID_ARGUMENT);
    CHECK(tl_create(4, TL_MAX_WINDOW + 1, &p) == TL_ERR_INVALID_ARGUMENT);
    CHECK(tl_create(4, 5, nullptr) == TL_ERR_NULL_ARGUMENT);
    CHECK(tl_destroy(nullptr) == TL_ERR_NULL_ARGUMENT);
    CHECK(tl_push(nullptr, 0, 1.0, nullptr) == TL_ERR_NULL_ARGUMENT);
    tl_stats s{};
    CHECK(tl_get_stats(nullptr, 0, &s) == TL_ERR_NULL_ARGUMENT);
    Proc pr(2, 3);
    CHECK(tl_push(pr.p, 2, 1.0, &s) == TL_ERR_CHANNEL_OUT_OF_RANGE);
    CHECK(tl_push(pr.p, -1, 1.0, &s) == TL_ERR_CHANNEL_OUT_OF_RANGE);
    CHECK(tl_get_stats(pr.p, 0, nullptr) == TL_ERR_NULL_ARGUMENT);
    CHECK(std::string(tl_status_message(TL_INVALID_VALUE)).size() > 0);
    CHECK(std::string(tl_status_message(12345)) == "unknown status");
    CHECK(tl_abi_version() == 1);
    CHECK(sizeof(tl_stats) == 40);
}

}  // namespace

int main() {
    struct T { const char* name; std::function<void()> fn; };
    const T tests[] = {
        {"known_sequence", test_known_sequence},
        {"window_size_one", test_window_size_one},
        {"warm_up", test_warm_up},
        {"reset", test_reset},
        {"invalid_values", test_invalid_values},
        {"against_reference_random", test_against_reference_random},
        {"drift_long_run", test_drift_long_run},
        {"batch", test_batch},
        {"argument_errors", test_argument_errors},
    };
    int failed_tests = 0;
    for (const auto& t : tests) {
        int before = g_failures;
        t.fn();
        bool ok = g_failures == before;
        if (!ok) ++failed_tests;
        std::printf("[%s] %s\n", ok ? "PASS" : "FAIL", t.name);
    }
    std::printf("%d tests, %d checks, %d failed checks\n", static_cast<int>(sizeof(tests) / sizeof(tests[0])),
                g_checks, g_failures);
    return failed_tests == 0 ? 0 : 1;
}
