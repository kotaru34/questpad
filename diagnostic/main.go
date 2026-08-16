package main

import (
    "encoding/binary"
    "flag"
    "fmt"
    "io"
    "math"
    "net"
    "os"
    "os/exec"
    "time"
)

const (
    packetSize = 68
    magic      = 0x44415051
    protocol   = 1
)

func batteryText(packed uint32, validBit uint, shift uint) string {
    if packed&(1<<validBit) == 0 {
        return "n/a"
    }
    return fmt.Sprintf("%d%%", (packed>>shift)&0xff)
}

func main() {
    adb := flag.String("adb", "adb", "path to adb.exe")
    serial := flag.String("serial", "", "ADB device serial (optional)")
    noADB := flag.Bool("no-adb", false, "skip adb forward; connect directly to localhost:38888")
    flag.Parse()

    if !*noADB {
        args := []string{}
        if *serial != "" { args = append(args, "-s", *serial) }
        args = append(args, "forward", "tcp:38888", "tcp:38888")
        cmd := exec.Command(*adb, args...)
        if out, err := cmd.CombinedOutput(); err != nil {
            fmt.Fprintf(os.Stderr, "adb forward failed: %v\n%s\n", err, out)
            os.Exit(1)
        }
    }

    for {
        c, err := net.DialTimeout("tcp", "127.0.0.1:38888", 2*time.Second)
        if err != nil {
            fmt.Printf("waiting for QuestPad... %v\n", err)
            time.Sleep(time.Second)
            continue
        }
        fmt.Println("connected")
        _ = c.SetReadDeadline(time.Now().Add(300 * time.Millisecond))
        buf := make([]byte, packetSize)
        var prev uint32
        havePrev := false
        var dropped uint64

        for {
            _ = c.SetReadDeadline(time.Now().Add(300 * time.Millisecond))
            if _, err := io.ReadFull(c, buf); err != nil {
                fmt.Printf("\ndisconnected/watchdog: %v\n", err)
                _ = c.Close()
                break
            }
            if binary.LittleEndian.Uint32(buf[0:4]) != magic ||
                binary.LittleEndian.Uint16(buf[4:6]) != protocol ||
                binary.LittleEndian.Uint16(buf[6:8]) != packetSize {
                fmt.Printf("\ninvalid packet header\n")
                _ = c.Close()
                break
            }

            seq := binary.LittleEndian.Uint32(buf[8:12])
            flags := binary.LittleEndian.Uint32(buf[12:16])
            thermal := int32(binary.LittleEndian.Uint32(buf[24:28]))
            f := func(off int) float32 {
                return math.Float32frombits(binary.LittleEndian.Uint32(buf[28+off : 32+off]))
            }
            buttons := binary.LittleEndian.Uint32(buf[60:64])
            battery := binary.LittleEndian.Uint32(buf[64:68])

            if havePrev {
                delta := seq - prev
                if delta > 1 && delta < 0x80000000 { dropped += uint64(delta - 1) }
            }
            prev, havePrev = seq, true

            fmt.Printf("\rseq=%-8d flags=%02x L=(%+.2f,%+.2f) R=(%+.2f,%+.2f) LT=%.2f RT=%.2f LG=%.2f RG=%.2f btn=%02x bat=%s/%s therm=%d drops=%d   ",
                seq, flags, f(0), f(4), f(8), f(12), f(16), f(20), f(24), f(28), buttons,
                batteryText(battery, 16, 0), batteryText(battery, 17, 8), thermal, dropped)
        }
        time.Sleep(500 * time.Millisecond)
    }
}
