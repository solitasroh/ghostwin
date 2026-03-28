/* Raw ghostty C API test — no wrapper, exact copy of official example pattern */
#include <stdio.h>
#include <string.h>
#include <ghostty/vt.h>

int main(void) {
    printf("Raw ghostty test\n");
    fflush(stdout);

    GhosttyTerminal terminal;
    GhosttyTerminalOptions opts = {0};
    opts.cols = 80;
    opts.rows = 24;
    opts.max_scrollback = 0;

    printf("Calling ghostty_terminal_new...\n");
    fflush(stdout);

    GhosttyResult result = ghostty_terminal_new(NULL, &terminal, opts);

    printf("Result: %d, terminal: %p\n", result, (void*)terminal);
    fflush(stdout);

    if (result != GHOSTTY_SUCCESS) {
        printf("FAIL: ghostty_terminal_new returned %d\n", result);
        return 1;
    }

    const char* text = "Hello ghostty!";
    ghostty_terminal_vt_write(terminal, (const uint8_t*)text, strlen(text));
    printf("Write OK\n");

    ghostty_terminal_free(terminal);
    printf("PASS\n");
    return 0;
}
