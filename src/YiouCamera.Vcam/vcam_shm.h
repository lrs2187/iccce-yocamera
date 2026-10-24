// vcam_shm.h - Shared memory frame contract between the producer (.NET / FFmpeg side)
//               and the DirectShow virtual camera filter.
//
// Layout:  [ VcamShmHeader (256 bytes) ][ slot0 ][ slot1 ] ... [ slot N-1 ]
//
// Lock-free "seqlock" publication:
//   producer: writeSeq++ (odd = writing) -> memcpy into slot -> writeSeq++ (even = stable)
//   consumer: seq1 = writeSeq; if (seq1 & 1) skip; slot = (seq1/2 - 1) % slotCount;
//             memcpy out; seq2 = writeSeq; if (seq2 != seq1) frame was torn -> skip
//
// All integers little-endian (Windows x64). This header is the single source of truth;
// the identical layout is documented in _research/virtual-camera.md.
#pragma once

#include <stdint.h>

#define VCAM_SHM_MAGIC        0x4D414356u   /* 'VCAM' */
#define VCAM_SHM_VERSION      1u
#define VCAM_SHM_NAME         L"Local\\YoCameraVcamFrames"
#define VCAM_SHM_SLOTS        4
#define VCAM_SHM_HEADER_BYTES 256

#ifdef __cplusplus
extern "C" {
#endif

/* pixel formats carried in the shared memory ring */
enum VcamPixelFormat
{
    VCAM_FMT_RGB24 = 0,   /* BGR byte order, **top-down rows** (row 0 = top row, per
                             docs/CONTRACTS.md section 1). The filter flips to the
                             bottom-up DirectShow RGB24 buffer itself. */
    VCAM_FMT_RGB32 = 1,   /* BGRA, top-down */
    VCAM_FMT_YUY2  = 2    /* packed 4:2:2, top-down */
};

#pragma pack(push, 1)
typedef struct VcamShmHeader
{
    uint32_t magic;          /* 0   VCAM_SHM_MAGIC                     */
    uint32_t version;        /* 4   VCAM_SHM_VERSION                   */
    uint32_t headerBytes;    /* 8   sizeof(VcamShmHeader) == 256       */
    uint32_t slotCount;      /* 12  ring slot count (2..8)             */
    uint32_t slotBytes;      /* 16  bytes per slot, >= stride*height   */
    uint32_t width;          /* 20  pixels                             */
    uint32_t height;         /* 24  pixels                             */
    uint32_t stride;         /* 28  bytes per row inside a slot        */
    uint32_t format;         /* 32  VcamPixelFormat                    */
    uint32_t fpsNum;         /* 36  e.g. 30                            */
    uint32_t fpsDen;         /* 40  e.g. 1                             */
    volatile int32_t writeSeq; /* 44 seqlock publication counter       */
    volatile int32_t readSeq;  /* 48 consumer progress (diagnostics)   */
    int64_t  timestamp100ns; /* 52  producer presentation time         */
    uint64_t frameIndex;     /* 60  monotonically increasing frame no. */
    uint32_t flags;          /* 68  bit0 = producer alive              */
    uint8_t  pad[VCAM_SHM_HEADER_BYTES - 72];
} VcamShmHeader;
#pragma pack(pop)

#ifdef __cplusplus
}
#endif

#if defined(__cplusplus)
static_assert(sizeof(VcamShmHeader) == VCAM_SHM_HEADER_BYTES, "VcamShmHeader must be 256 bytes");
#endif
