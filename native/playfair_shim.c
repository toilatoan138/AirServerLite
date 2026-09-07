/*
 * playfair_shim.c - a stable C ABI over the FairPlay SAP v3 reference implementation.
 *
 * WHY THIS FILE EXISTS
 * --------------------
 * FairPlay is the handshake iOS insists on before it will send a single video frame. It is
 * not an algorithm anyone can derive from a specification: it is a fixed set of response
 * blobs and key-derivation tables lifted out of Apple firmware. Every open-source AirPlay
 * receiver carries the same reference C code for it.
 *
 * Rather than transcribe those byte tables into C# - where one wrong nibble produces a
 * session that dies silently with no error anywhere - AirServerLite P/Invokes the reference
 * code directly through the four functions below.
 *
 * BUILD
 * -----
 *   pwsh -File native/build-playfair.ps1
 *
 * The script locates the MSVC toolset, compiles this file together with UxPlay's
 * lib/fairplay_playfair.c, lib/logger.c and lib/playfair/*.c, and drops playfair.dll into
 * the application output directory. No CMake required.
 *
 * REFERENCE API (verified against UxPlay lib/fairplay.h)
 * -----------------------------------------------------
 *     fairplay_t *fairplay_init(logger_t *logger);
 *     int  fairplay_setup    (fairplay_t *fp, const unsigned char req[16],  unsigned char res[142]);
 *     int  fairplay_handshake(fairplay_t *fp, const unsigned char req[164], unsigned char res[32]);
 *     int  fairplay_decrypt  (fairplay_t *fp, const unsigned char input[72], unsigned char output[16]);
 *     void fairplay_destroy  (fairplay_t *fp);
 *
 * Note the reference splits the two /fp-setup messages across two functions and writes into
 * a caller-supplied buffer. fp_setup() below presents them as the single request/response
 * operation the RTSP layer actually sees, dispatching on request length.
 */

#include <stdlib.h>
#include <string.h>

#include "fairplay.h"
#include "logger.h"

#ifdef _WIN32
#define FP_EXPORT __declspec(dllexport)
#else
#define FP_EXPORT __attribute__((visibility("default")))
#endif

/* Response sizes are fixed by the protocol, not by our buffer. */
#define FP_MSG1_REQUEST_LEN   16
#define FP_MSG1_RESPONSE_LEN  142
#define FP_MSG2_REQUEST_LEN   164
#define FP_MSG2_RESPONSE_LEN  32

typedef struct
{
    fairplay_t *impl;
    logger_t   *logger;
} fp_handle_t;

FP_EXPORT void *fp_create(void)
{
    fp_handle_t *h = (fp_handle_t *)calloc(1, sizeof(fp_handle_t));
    if (!h) return NULL;

    /* The reference code logs through its own logger object. Give it a quiet one so
     * FairPlay internals do not write to the stdout of a GUI process. */
    h->logger = logger_init();
    if (h->logger) logger_set_level(h->logger, LOGGER_ERR);

    h->impl = fairplay_init(h->logger);
    if (!h->impl)
    {
        if (h->logger) logger_destroy(h->logger);
        free(h);
        return NULL;
    }
    return h;
}

/*
 * One /fp-setup message in, one response out.
 *   message 1:  16 bytes in -> 142 bytes out
 *   message 2: 164 bytes in ->  32 bytes out
 * Returns 0 on success, negative on failure.
 */
FP_EXPORT int fp_setup(void *handle,
                       const unsigned char *request, int request_len,
                       unsigned char *response, int response_capacity,
                       int *response_len)
{
    fp_handle_t *h = (fp_handle_t *)handle;
    int rc;

    if (!h || !h->impl || !request || !response || !response_len) return -1;

    if (request_len == FP_MSG1_REQUEST_LEN)
    {
        if (response_capacity < FP_MSG1_RESPONSE_LEN) return -4;
        rc = fairplay_setup(h->impl, request, response);
        if (rc != 0) return -3;
        *response_len = FP_MSG1_RESPONSE_LEN;
        return 0;
    }

    if (request_len == FP_MSG2_REQUEST_LEN)
    {
        if (response_capacity < FP_MSG2_RESPONSE_LEN) return -4;
        rc = fairplay_handshake(h->impl, request, response);
        if (rc != 0) return -3;
        *response_len = FP_MSG2_RESPONSE_LEN;
        return 0;
    }

    return -2; /* iOS sent a length we do not recognise - log it, do not guess */
}

/*
 * Unwrap the 72-byte "ekey" from RTSP SETUP into the 16-byte session AES key.
 * Returns 0 on success.
 */
FP_EXPORT int fp_decrypt(void *handle, const unsigned char *ekey72, unsigned char *aeskey16)
{
    fp_handle_t *h = (fp_handle_t *)handle;
    if (!h || !h->impl || !ekey72 || !aeskey16) return -1;
    return fairplay_decrypt(h->impl, ekey72, aeskey16) == 0 ? 0 : -2;
}

FP_EXPORT void fp_destroy(void *handle)
{
    fp_handle_t *h = (fp_handle_t *)handle;
    if (!h) return;
    if (h->impl) fairplay_destroy(h->impl);
    if (h->logger) logger_destroy(h->logger);
    free(h);
}
