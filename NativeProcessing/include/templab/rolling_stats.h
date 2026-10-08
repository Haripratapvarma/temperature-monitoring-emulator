/*
 * templab_native - per-channel rolling statistics with a C ABI.
 *
 * ABI rules
 *  - Only fixed-width integer types, double and pointers to plain structs cross the boundary.
 *  - Every function returns a tl_status (int32_t). No C++ exception ever escapes.
 *  - Buffers are described by (pointer, element count). Counts are int32_t.
 *  - A tl_processor is created by tl_create and must be released exactly once with tl_destroy.
 *  - A processor is NOT thread-safe; the caller guarantees one thread uses it at a time
 *    (the managed processing worker owns it).
 */
#ifndef TEMPLAB_ROLLING_STATS_H
#define TEMPLAB_ROLLING_STATS_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(TEMPLAB_NATIVE_BUILD)
#    define TL_API __declspec(dllexport)
#  else
#    define TL_API __declspec(dllimport)
#  endif
#  define TL_CALL __cdecl
#else
#  define TL_API __attribute__((visibility("default")))
#  define TL_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef int32_t tl_status;

enum {
    TL_OK = 0,
    TL_ERR_NULL_ARGUMENT = 1,
    TL_ERR_INVALID_ARGUMENT = 2,  /* bad channel count / window size */
    TL_ERR_CHANNEL_OUT_OF_RANGE = 3,
    TL_ERR_BUFFER_TOO_SMALL = 4,
    TL_ERR_ALLOCATION = 5,
    TL_ERR_INTERNAL = 6,
    TL_INVALID_VALUE = 7          /* sample rejected by invalid-data policy; stats unchanged */
};

#define TL_MAX_CHANNELS 64
#define TL_MAX_WINDOW 100000
#define TL_MIN_VALID_C (-273.15)
#define TL_MAX_VALID_C (2000.0)

/* Statistics for one channel. 40 bytes, no padding surprises: 3 doubles then 4 int32. */
typedef struct tl_stats {
    double mean;
    double min;
    double max;
    int32_t count;        /* valid samples currently in the window (0..window_size) */
    int32_t window_size;
    int32_t is_warm;      /* 1 when count == window_size */
    int32_t last_status;  /* TL_OK or TL_INVALID_VALUE for the sample that produced this result */
} tl_stats;

typedef struct tl_processor tl_processor;

TL_API tl_status TL_CALL tl_create(int32_t channel_count, int32_t window_size, tl_processor** out_processor);
TL_API tl_status TL_CALL tl_destroy(tl_processor* processor);

/* Push one value. out_stats may be NULL. Returns TL_INVALID_VALUE (not an error) for rejected values. */
TL_API tl_status TL_CALL tl_push(tl_processor* processor, int32_t channel, double value, tl_stats* out_stats);

/*
 * Push `count` values. channels[i]/values[i] pair up. out_stats (capacity out_capacity) receives one
 * result per input when non-NULL; out_capacity must be >= count. Rejected values do not stop the batch.
 * Returns the first hard error, otherwise TL_OK. out_invalid_count (may be NULL) receives the rejected count.
 */
TL_API tl_status TL_CALL tl_push_batch(tl_processor* processor,
                                       const int32_t* channels,
                                       const double* values,
                                       int32_t count,
                                       tl_stats* out_stats,
                                       int32_t out_capacity,
                                       int32_t* out_invalid_count);

TL_API tl_status TL_CALL tl_get_stats(const tl_processor* processor, int32_t channel, tl_stats* out_stats);

/* channel = -1 resets every channel. */
TL_API tl_status TL_CALL tl_reset(tl_processor* processor, int32_t channel);

/* Static, never NULL. */
TL_API const char* TL_CALL tl_status_message(tl_status status);

TL_API int32_t TL_CALL tl_abi_version(void);

#ifdef __cplusplus
}
#endif

#endif
