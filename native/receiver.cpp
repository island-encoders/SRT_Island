#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <fcntl.h>
#include <io.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <string>
#include <sstream>
#include <vector>
#include <srt.h>

class Runtime {
public:
    Runtime() : ready(srt_startup() == 0) {}
    ~Runtime() { if (ready) srt_cleanup(); }
    const bool ready;
};

class Socket {
public:
    explicit Socket(SRTSOCKET value) : id(value) {}
    ~Socket() { if (id != SRT_INVALID_SOCK) srt_close(id); }
    const SRTSOCKET id;
    Socket(const Socket&) = delete;
    Socket& operator=(const Socket&) = delete;
};

static int report(const char* stage)
{
    std::fprintf(stderr, "%s: %s\n", stage, srt_getlasterror_str());
    return 1;
}

static bool configure(SRTSOCKET listener, const addrinfo& address, int latency, const char* secret)
{
    const int yes = 1;
    if (srt_setsockflag(listener, SRTO_GROUPCONNECT, &yes, sizeof yes) ||
        srt_setsockflag(listener, SRTO_LATENCY, &latency, sizeof latency))
        return false;
    if (secret && *secret &&
        srt_setsockflag(listener, SRTO_PASSPHRASE, secret, static_cast<int>(std::strlen(secret))))
        return false;
    return srt_bind(listener, address.ai_addr, static_cast<int>(address.ai_addrlen)) == 0 &&
        srt_listen(listener, 8) == 0;
}

static int receive(const std::vector<SRTSOCKET>& listeners)
{
    std::fprintf(stderr, "Waiting for an SRT bonding group.\n");
    for (;;)
    {
        const Socket group(listeners.size() == 1 ? srt_accept(listeners.front(), nullptr, nullptr) :
            srt_accept_bond(listeners.data(), static_cast<int>(listeners.size()), -1));
        if (group.id == SRT_INVALID_SOCK) return report("Accept connection");
        if (!(group.id & SRTGROUP_MASK))
        {
            std::fprintf(stderr, "Ignored a caller without SRT bonding.\n");
            continue;
        }
        std::fprintf(stderr, "Accepted an SRT bonding group.\n");
        char packet[65536];
        size_t previous = 0;
        for (;;)
        {
            SRT_SOCKGROUPDATA members[32];
            SRT_MSGCTRL control = srt_msgctrl_default;
            control.grpdata = members;
            control.grpdata_size = sizeof members / sizeof *members;
            const auto length = srt_recvmsg2(group.id, packet, sizeof packet, &control);
            if (length <= 0) return report("Bonded group ended");
            if (control.grpdata_size != previous)
            {
                previous = control.grpdata_size;
                std::fprintf(stderr, "SRT bonding group members: %zu.\n", previous);
            }
            if (std::fwrite(packet, 1, length, stdout) != static_cast<size_t>(length) ||
                std::fflush(stdout) != 0) return 0;
        }
    }
}

int main(int argc, char** argv)
{
    if (argc != 4)
    {
        std::fprintf(stderr, "Usage: srt-island-bond ADDRESS[,ADDRESS] PORT LATENCY_MS\n");
        return 2;
    }
    const Runtime runtime;
    if (!runtime.ready) return report("Initialize SRT");
    _setmode(_fileno(stdout), _O_BINARY);
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_DGRAM;
    hints.ai_flags = AI_PASSIVE;
    std::vector<std::unique_ptr<Socket>> owned;
    std::vector<SRTSOCKET> listeners;
    std::istringstream targets(argv[1]);
    std::string host;
    while (std::getline(targets, host, ','))
    {
        addrinfo* address = nullptr;
        if (getaddrinfo(host.c_str(), argv[2], &hints, &address) || !address)
        {
            std::fprintf(stderr, "Could not resolve bonded listen address %s.\n", host.c_str());
            return 2;
        }
        auto socket = std::make_unique<Socket>(srt_create_socket());
        const auto ready = socket->id != SRT_INVALID_SOCK &&
            configure(socket->id, *address, std::atoi(argv[3]), std::getenv("SRT_ISLAND_BOND_PASSPHRASE"));
        freeaddrinfo(address);
        if (!ready) return report("Configure and bind bonded listener");
        listeners.push_back(socket->id);
        owned.push_back(std::move(socket));
    }
    return listeners.empty() ? 2 : receive(listeners);
}
