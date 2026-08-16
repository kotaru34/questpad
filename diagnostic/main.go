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
    "strings"
    "time"
)

const (
    packetSize    = 152
    magic         = 0x44415051
    protocol      = 2
    feedbackMagic = 0x31424651
)

func batteryText(packed uint32, validBit uint, shift uint) string {
    if packed&(1<<validBit) == 0 { return "n/a" }
    return fmt.Sprintf("%d%%", (packed>>shift)&0xff)
}

func motionControl(name string) uint16 {
    switch strings.ToLower(name) {
    case "rate", "angular":
        return 1
    case "camera", "tracked":
        return 2
    case "both", "wheel":
        return 3
    default:
        return 0
    }
}

func f32(buf []byte, off int) float32 {
    return math.Float32frombits(binary.LittleEndian.Uint32(buf[off : off+4]))
}

func main() {
    adb := flag.String("adb", "adb", "path to adb.exe")
    serial := flag.String("serial", "", "ADB device serial (optional)")
    noADB := flag.Bool("no-adb", false, "skip adb forward; connect directly to localhost:38888")
    motion := flag.String("motion", "off", "motion request: off|rate|camera|both")
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

    control := motionControl(*motion)
    for {
        c, err := net.DialTimeout("tcp", "127.0.0.1:38888", 2*time.Second)
        if err != nil {
            fmt.Printf("waiting for QuestPad... %v\n", err)
            time.Sleep(time.Second)
            continue
        }
        fmt.Printf("connected (protocol v2, motion=%s/%d)\n", *motion, control)

        stopFeedback := make(chan struct{})
        go func(conn net.Conn) {
            ticker := time.NewTicker(100 * time.Millisecond)
            defer ticker.Stop()
            feedback := make([]byte, 8)
            binary.LittleEndian.PutUint32(feedback[0:4], feedbackMagic)
            binary.LittleEndian.PutUint16(feedback[6:8], control)
            for {
                select {
                case <-ticker.C:
                    _ = conn.SetWriteDeadline(time.Now().Add(100 * time.Millisecond))
                    if _, err := conn.Write(feedback); err != nil { return }
                case <-stopFeedback:
                    return
                }
            }
        }(c)

        buf := make([]byte, packetSize)
        var prev uint32
        havePrev := false
        var dropped uint64

        for {
            _ = c.SetReadDeadline(time.Now().Add(300 * time.Millisecond))
            if _, err := io.ReadFull(c, buf); err != nil {
                fmt.Printf("\ndisconnected/watchdog: %v\n", err)
                close(stopFeedback)
                _ = c.Close()
                break
            }
            if binary.LittleEndian.Uint32(buf[0:4]) != magic ||
                binary.LittleEndian.Uint16(buf[4:6]) != protocol ||
                binary.LittleEndian.Uint16(buf[6:8]) != packetSize {
                fmt.Printf("\ninvalid packet header\n")
                close(stopFeedback)
                _ = c.Close()
                break
            }

            seq := binary.LittleEndian.Uint32(buf[8:12])
            flags := binary.LittleEndian.Uint32(buf[12:16])
            thermal := int32(binary.LittleEndian.Uint32(buf[24:28]))
            buttons := binary.LittleEndian.Uint32(buf[60:64])
            battery := binary.LittleEndian.Uint32(buf[64:68])
            mf := binary.LittleEndian.Uint32(buf[68:72])

            if havePrev {
                delta := seq - prev
                if delta > 1 && delta < 0x80000000 { dropped += uint64(delta - 1) }
            }
            prev, havePrev = seq, true

            // Right controller local angular velocity starts at byte 140.
            wx, wy, wz := f32(buf, 140), f32(buf, 144), f32(buf, 148)
            ptL := (mf & (1 << 4)) != 0
            ptR := (mf & (1 << 12)) != 0
            avR := (mf & (1 << 13)) != 0

            fmt.Printf("\rseq=%-8d flags=%02x L=(%+.2f,%+.2f) R=(%+.2f,%+.2f) LT=%.2f RT=%.2f LG=%.2f RG=%.2f btn=%02x bat=%s/%s therm=%d motion=%08x PT=%t/%t AVR=%t wR=(%+.3f,%+.3f,%+.3f) drops=%d   ",
                seq, flags, f32(buf, 28), f32(buf, 32), f32(buf, 36), f32(buf, 40),
                f32(buf, 44), f32(buf, 48), f32(buf, 52), f32(buf, 56), buttons,
                batteryText(battery, 16, 0), batteryText(battery, 17, 8), thermal,
                mf, ptL, ptR, avR, wx, wy, wz, dropped)
        }
        time.Sleep(500 * time.Millisecond)
    }
}
