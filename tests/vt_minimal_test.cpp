#include "vt_bridge.h"
#include <cstdio>

int main() {
    printf("Step 1: vt_bridge_terminal_new(80, 24, 0)...\n");
    fflush(stdout);

    void* term = vt_bridge_terminal_new(80, 24, 0);
    printf("Step 2: terminal = %p\n", term);
    fflush(stdout);

    if (!term) {
        printf("FAIL: terminal is NULL\n");
        return 1;
    }

    printf("Step 3: vt_bridge_terminal_free...\n");
    fflush(stdout);
    vt_bridge_terminal_free(term);

    printf("PASS\n");
    return 0;
}
