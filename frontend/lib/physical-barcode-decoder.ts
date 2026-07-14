export interface PhysicalBarcodeDetection {
    rawValue: string;
    format: "ean_13" | "upc_a";
}

export interface PhysicalBarcodeDecoderSession {
    stop(): void;
}

interface NativeBarcode {
    rawValue: string;
    format: string;
}

interface NativeBarcodeDetectorInstance {
    detect(source: HTMLVideoElement): Promise<NativeBarcode[]>;
}

interface NativeBarcodeDetectorConstructor {
    new(options: { formats: string[] }): NativeBarcodeDetectorInstance;
    getSupportedFormats(): Promise<string[]>;
}

const RETAIL_FORMATS = ["ean_13", "upc_a"] as const;

/**
 * Starts a decoder for an already-authorized local camera stream. Native BarcodeDetector is used
 * only when it reports both required retail formats; otherwise ZXing is loaded on demand. No
 * frame or decoded value leaves the browser through this abstraction.
 */
export async function startPhysicalBarcodeDecoder(
    video: HTMLVideoElement,
    stream: MediaStream,
    onDetected: (detection: PhysicalBarcodeDetection) => void,
): Promise<PhysicalBarcodeDecoderSession> {
    const nativeConstructor = (
        globalThis as typeof globalThis & { BarcodeDetector?: NativeBarcodeDetectorConstructor }
    ).BarcodeDetector;

    if (nativeConstructor) {
        try {
            const supportedFormats = await nativeConstructor.getSupportedFormats();
            if (RETAIL_FORMATS.every((format) => supportedFormats.includes(format))) {
                return startNativeDecoder(nativeConstructor, video, stream, onDetected);
            }
        } catch {
            // A broken or partially implemented native API falls through to the tested decoder.
        }
    }

    return startZxingDecoder(video, stream, onDetected);
}

async function startNativeDecoder(
    Detector: NativeBarcodeDetectorConstructor,
    video: HTMLVideoElement,
    stream: MediaStream,
    onDetected: (detection: PhysicalBarcodeDetection) => void,
): Promise<PhysicalBarcodeDecoderSession> {
    const detector = new Detector({ formats: [...RETAIL_FORMATS] });
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    video.srcObject = stream;
    await video.play();

    const detectNextFrame = async () => {
        if (stopped) return;

        try {
            const detections = await detector.detect(video);
            const supported = detections.find(isPhysicalRetailDetection);
            if (supported) {
                stopped = true;
                onDetected({ rawValue: supported.rawValue, format: supported.format });
                return;
            }
        } catch {
            // Individual frames commonly fail to decode; scanning continues without logging data.
        }

        if (!stopped) timer = setTimeout(() => void detectNextFrame(), 150);
    };

    void detectNextFrame();
    return {
        stop() {
            stopped = true;
            if (timer) clearTimeout(timer);
        },
    };
}

async function startZxingDecoder(
    video: HTMLVideoElement,
    stream: MediaStream,
    onDetected: (detection: PhysicalBarcodeDetection) => void,
): Promise<PhysicalBarcodeDecoderSession> {
    const { BarcodeFormat, BrowserMultiFormatOneDReader } = await import("@zxing/browser");
    const reader = new BrowserMultiFormatOneDReader();
    const controls = await reader.decodeFromStream(stream, video, (result) => {
        if (!result) return;

        const format = result.getBarcodeFormat();
        if (format === BarcodeFormat.EAN_13) {
            onDetected({ rawValue: result.getText(), format: "ean_13" });
        } else if (format === BarcodeFormat.UPC_A) {
            onDetected({ rawValue: result.getText(), format: "upc_a" });
        }
    });

    return { stop: () => controls.stop() };
}

function isPhysicalRetailDetection(
    detection: NativeBarcode,
): detection is NativeBarcode & { format: PhysicalBarcodeDetection["format"] } {
    return RETAIL_FORMATS.includes(detection.format as PhysicalBarcodeDetection["format"]);
}
