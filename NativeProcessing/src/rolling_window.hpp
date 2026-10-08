// Internal C++ implementation. Not part of the ABI.
#pragma once

#include <cmath>
#include <cstdint>
#include <deque>
#include <stdexcept>
#include <vector>

namespace templab {

inline bool is_valid_temperature(double v, double lo, double hi) noexcept {
    return std::isfinite(v) && v >= lo && v <= hi;
}

// Rolling mean/min/max over the last `capacity` values.
//  - push:  O(1) amortised (monotonic deques for min/max, running sum for mean)
//  - memory: O(capacity)
// The running sum is recomputed from the buffer once per `capacity` pushes so floating-point error
// cannot accumulate without bound during long sessions.
class RollingWindow {
public:
    explicit RollingWindow(std::size_t capacity) : buffer_(capacity, 0.0), capacity_(capacity) {
        if (capacity == 0) throw std::invalid_argument("capacity must be > 0");
    }

    void push(double value) {
        const std::uint64_t idx = next_index_++;
        if (count_ == capacity_) {
            sum_ -= buffer_[head_];
        } else {
            ++count_;
        }
        buffer_[head_] = value;
        head_ = (head_ + 1) % capacity_;
        sum_ += value;

        const std::uint64_t oldest = idx + 1 >= capacity_ ? idx + 1 - capacity_ : 0;
        while (!min_q_.empty() && min_q_.back().value >= value) min_q_.pop_back();
        min_q_.push_back({idx, value});
        while (min_q_.front().index < oldest) min_q_.pop_front();

        while (!max_q_.empty() && max_q_.back().value <= value) max_q_.pop_back();
        max_q_.push_back({idx, value});
        while (max_q_.front().index < oldest) max_q_.pop_front();

        if (++since_resum_ >= capacity_) {
            resum();
        }
    }

    void reset() noexcept {
        head_ = 0;
        count_ = 0;
        next_index_ = 0;
        sum_ = 0.0;
        since_resum_ = 0;
        min_q_.clear();
        max_q_.clear();
    }

    std::size_t count() const noexcept { return count_; }
    std::size_t capacity() const noexcept { return capacity_; }
    bool warm() const noexcept { return count_ == capacity_; }
    double mean() const noexcept { return count_ ? sum_ / static_cast<double>(count_) : std::nan(""); }
    double min() const noexcept { return count_ ? min_q_.front().value : std::nan(""); }
    double max() const noexcept { return count_ ? max_q_.front().value : std::nan(""); }

private:
    struct Entry {
        std::uint64_t index;
        double value;
    };

    void resum() noexcept {
        // Sum in insertion order (oldest first) so the result matches a naive reference exactly
        // at the moment of re-summation.
        double s = 0.0;
        std::size_t start = (head_ + capacity_ - count_) % capacity_;
        for (std::size_t i = 0; i < count_; ++i) s += buffer_[(start + i) % capacity_];
        sum_ = s;
        since_resum_ = 0;
    }

    std::vector<double> buffer_;
    std::size_t capacity_;
    std::size_t head_ = 0;
    std::size_t count_ = 0;
    std::uint64_t next_index_ = 0;
    std::size_t since_resum_ = 0;
    double sum_ = 0.0;
    std::deque<Entry> min_q_;
    std::deque<Entry> max_q_;
};

class MultiChannelProcessor {
public:
    MultiChannelProcessor(std::size_t channels, std::size_t window) {
        windows_.reserve(channels);
        for (std::size_t i = 0; i < channels; ++i) windows_.emplace_back(window);
    }

    std::size_t channel_count() const noexcept { return windows_.size(); }
    RollingWindow& at(std::size_t ch) { return windows_.at(ch); }
    const RollingWindow& at(std::size_t ch) const { return windows_.at(ch); }
    void reset_all() noexcept {
        for (auto& w : windows_) w.reset();
    }

private:
    std::vector<RollingWindow> windows_;
};

}  // namespace templab
