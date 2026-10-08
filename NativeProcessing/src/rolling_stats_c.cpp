// C ABI over templab::MultiChannelProcessor. Every entry point catches all exceptions.
#include "templab/rolling_stats.h"

#include <new>

#include "rolling_window.hpp"

struct tl_processor {
    templab::MultiChannelProcessor impl;
    tl_processor(std::size_t channels, std::size_t window) : impl(channels, window) {}
};

namespace {

void fill(const templab::RollingWindow& w, tl_status last, tl_stats* out) noexcept {
    out->mean = w.mean();
    out->min = w.min();
    out->max = w.max();
    out->count = static_cast<int32_t>(w.count());
    out->window_size = static_cast<int32_t>(w.capacity());
    out->is_warm = w.warm() ? 1 : 0;
    out->last_status = last;
}

tl_status push_one(tl_processor* p, int32_t channel, double value, tl_stats* out) {
    if (channel < 0 || static_cast<std::size_t>(channel) >= p->impl.channel_count()) {
        return TL_ERR_CHANNEL_OUT_OF_RANGE;
    }
    auto& w = p->impl.at(static_cast<std::size_t>(channel));
    tl_status st = TL_OK;
    if (templab::is_valid_temperature(value, TL_MIN_VALID_C, TL_MAX_VALID_C)) {
        w.push(value);
    } else {
        st = TL_INVALID_VALUE;
    }
    if (out) fill(w, st, out);
    return st;
}

template <typename F>
tl_status guarded(F&& f) noexcept {
    try {
        return f();
    } catch (const std::bad_alloc&) {
        return TL_ERR_ALLOCATION;
    } catch (...) {
        return TL_ERR_INTERNAL;
    }
}

}  // namespace

extern "C" {

TL_API tl_status TL_CALL tl_create(int32_t channel_count, int32_t window_size, tl_processor** out_processor) {
    return guarded([&]() -> tl_status {
        if (!out_processor) return TL_ERR_NULL_ARGUMENT;
        *out_processor = nullptr;
        if (channel_count < 1 || channel_count > TL_MAX_CHANNELS) return TL_ERR_INVALID_ARGUMENT;
        if (window_size < 1 || window_size > TL_MAX_WINDOW) return TL_ERR_INVALID_ARGUMENT;
        *out_processor = new tl_processor(static_cast<std::size_t>(channel_count),
                                          static_cast<std::size_t>(window_size));
        return TL_OK;
    });
}

TL_API tl_status TL_CALL tl_destroy(tl_processor* processor) {
    return guarded([&]() -> tl_status {
        if (!processor) return TL_ERR_NULL_ARGUMENT;
        delete processor;
        return TL_OK;
    });
}

TL_API tl_status TL_CALL tl_push(tl_processor* processor, int32_t channel, double value, tl_stats* out_stats) {
    return guarded([&]() -> tl_status {
        if (!processor) return TL_ERR_NULL_ARGUMENT;
        return push_one(processor, channel, value, out_stats);
    });
}

TL_API tl_status TL_CALL tl_push_batch(tl_processor* processor, const int32_t* channels, const double* values,
                                       int32_t count, tl_stats* out_stats, int32_t out_capacity,
                                       int32_t* out_invalid_count) {
    return guarded([&]() -> tl_status {
        if (out_invalid_count) *out_invalid_count = 0;
        if (!processor) return TL_ERR_NULL_ARGUMENT;
        if (count < 0) return TL_ERR_INVALID_ARGUMENT;
        if (count == 0) return TL_OK;
        if (!channels || !values) return TL_ERR_NULL_ARGUMENT;
        if (out_stats && out_capacity < count) return TL_ERR_BUFFER_TOO_SMALL;
        // Validate every channel first so a bad batch changes nothing.
        for (int32_t i = 0; i < count; ++i) {
            if (channels[i] < 0 || static_cast<std::size_t>(channels[i]) >= processor->impl.channel_count()) {
                return TL_ERR_CHANNEL_OUT_OF_RANGE;
            }
        }
        int32_t invalid = 0;
        for (int32_t i = 0; i < count; ++i) {
            tl_status st = push_one(processor, channels[i], values[i], out_stats ? &out_stats[i] : nullptr);
            if (st == TL_INVALID_VALUE) ++invalid;
        }
        if (out_invalid_count) *out_invalid_count = invalid;
        return TL_OK;
    });
}

TL_API tl_status TL_CALL tl_get_stats(const tl_processor* processor, int32_t channel, tl_stats* out_stats) {
    return guarded([&]() -> tl_status {
        if (!processor || !out_stats) return TL_ERR_NULL_ARGUMENT;
        if (channel < 0 || static_cast<std::size_t>(channel) >= processor->impl.channel_count()) {
            return TL_ERR_CHANNEL_OUT_OF_RANGE;
        }
        fill(processor->impl.at(static_cast<std::size_t>(channel)), TL_OK, out_stats);
        return TL_OK;
    });
}

TL_API tl_status TL_CALL tl_reset(tl_processor* processor, int32_t channel) {
    return guarded([&]() -> tl_status {
        if (!processor) return TL_ERR_NULL_ARGUMENT;
        if (channel == -1) {
            processor->impl.reset_all();
            return TL_OK;
        }
        if (channel < 0 || static_cast<std::size_t>(channel) >= processor->impl.channel_count()) {
            return TL_ERR_CHANNEL_OUT_OF_RANGE;
        }
        processor->impl.at(static_cast<std::size_t>(channel)).reset();
        return TL_OK;
    });
}

TL_API const char* TL_CALL tl_status_message(tl_status status) {
    switch (status) {
        case TL_OK: return "ok";
        case TL_ERR_NULL_ARGUMENT: return "null argument";
        case TL_ERR_INVALID_ARGUMENT: return "invalid argument";
        case TL_ERR_CHANNEL_OUT_OF_RANGE: return "channel out of range";
        case TL_ERR_BUFFER_TOO_SMALL: return "output buffer too small";
        case TL_ERR_ALLOCATION: return "allocation failed";
        case TL_ERR_INTERNAL: return "internal error";
        case TL_INVALID_VALUE: return "value rejected by invalid-data policy";
        default: return "unknown status";
    }
}

TL_API int32_t TL_CALL tl_abi_version(void) { return 1; }

}  // extern "C"
