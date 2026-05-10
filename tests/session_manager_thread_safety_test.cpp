/// @file session_manager_thread_safety_test.cpp
/// Terminal Display Hardening: SessionManager registry lookup/mutation lock contract.

#include "session/session_manager.h"

#include <chrono>
#include <cstdio>
#include <future>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <vector>

namespace {

using namespace std::chrono_literals;

static int passed = 0, failed = 0;

#define TEST(name) \
    do { \
        std::printf("[TEST] %s... ", #name); \
        if (test_##name()) { std::printf("PASS\n"); ++passed; } \
        else { std::printf("FAIL\n"); ++failed; } \
    } while (0)

std::shared_ptr<ghostwin::Session> make_session(ghostwin::SessionId id) {
    auto session = std::make_shared<ghostwin::Session>();
    session->id = id;
    return session;
}

} // namespace

namespace ghostwin {

struct SessionManagerThreadSafetyTestAccess {
    static std::shared_mutex& registry_mutex(SessionManager& manager) {
        return manager.sessions_mutex_;
    }

    static void add_session(SessionManager& manager, std::shared_ptr<Session> session) {
        manager.sessions_.push_back(std::move(session));
    }

    static void set_active_index(SessionManager& manager, uint32_t index) {
        manager.active_idx_.store(index, std::memory_order_release);
    }
};

} // namespace ghostwin

namespace {

static bool test_lookup_waits_for_exclusive_registry_lock() {
    ghostwin::SessionManager manager;
    ghostwin::SessionManagerThreadSafetyTestAccess::add_session(manager, make_session(10));
    ghostwin::SessionManagerThreadSafetyTestAccess::add_session(manager, make_session(20));

    auto& mutex = ghostwin::SessionManagerThreadSafetyTestAccess::registry_mutex(manager);
    std::unique_lock<std::shared_mutex> hold(mutex);

    std::promise<void> started;
    auto future = std::async(std::launch::async, [&] {
        started.set_value();
        return manager.get(20);
    });
    started.get_future().wait();

    if (future.wait_for(50ms) != std::future_status::timeout) {
        std::printf("(get returned while registry was exclusively locked) ");
        return false;
    }

    hold.unlock();
    auto session = future.get();
    if (!session || session->id != 20) {
        std::printf("(wrong lookup result) ");
        return false;
    }
    return true;
}

static bool test_mutation_waits_for_shared_registry_lock() {
    ghostwin::SessionManager manager;
    ghostwin::SessionManagerThreadSafetyTestAccess::add_session(manager, make_session(10));
    ghostwin::SessionManagerThreadSafetyTestAccess::add_session(manager, make_session(20));
    ghostwin::SessionManagerThreadSafetyTestAccess::set_active_index(manager, 0);

    auto& mutex = ghostwin::SessionManagerThreadSafetyTestAccess::registry_mutex(manager);
    std::shared_lock<std::shared_mutex> hold(mutex);

    std::promise<void> started;
    auto future = std::async(std::launch::async, [&] {
        started.set_value();
        manager.move_session(0, 1);
    });
    started.get_future().wait();

    if (future.wait_for(50ms) != std::future_status::timeout) {
        std::printf("(move_session mutated while registry was shared-locked) ");
        return false;
    }

    hold.unlock();
    future.get();

    const auto ids = manager.ids();
    if (ids.size() != 2 || ids[0] != 20 || ids[1] != 10) {
        std::printf("(ids not reordered) ");
        return false;
    }
    if (manager.active_id() != 10) {
        std::printf("(active id changed after move) ");
        return false;
    }
    return true;
}

} // namespace

int main() {
    std::printf("=== SessionManager Thread Safety Test Suite ===\n\n");

    TEST(lookup_waits_for_exclusive_registry_lock);
    TEST(mutation_waits_for_shared_registry_lock);

    std::printf("\n=== Results: %d passed, %d failed ===\n", passed, failed);
    return failed > 0 ? 1 : 0;
}
