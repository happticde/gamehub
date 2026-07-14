import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { startPhysicalBarcodeDecoder } from "../lib/physical-barcode-decoder.ts";

test("native decoder is used only after runtime format support and returns retail data", async () => {
    const originalDetector = Object.getOwnPropertyDescriptor(globalThis, "BarcodeDetector");

    class FakeBarcodeDetector {
        static async getSupportedFormats() {
            return ["ean_13", "upc_a"];
        }

        async detect() {
            return [{ rawValue: "4006381333931", format: "ean_13" }];
        }
    }

    Object.defineProperty(globalThis, "BarcodeDetector", {
        configurable: true,
        value: FakeBarcodeDetector,
    });

    try {
        const video = {
            srcObject: null,
            async play() {},
        } as unknown as HTMLVideoElement;
        const detections: Array<{ rawValue: string; format: string }> = [];

        const session = await startPhysicalBarcodeDecoder(
            video,
            {} as MediaStream,
            (detection) => detections.push(detection),
        );
        await new Promise((resolve) => setTimeout(resolve, 0));
        session.stop();

        assert.deepEqual(detections, [{ rawValue: "4006381333931", format: "ean_13" }]);
        assert.ok(video.srcObject);
    } finally {
        if (originalDetector) {
            Object.defineProperty(globalThis, "BarcodeDetector", originalDetector);
        } else {
            Reflect.deleteProperty(globalThis, "BarcodeDetector");
        }
    }
});

test("native decoder ignores unsupported symbologies without exposing their value", async () => {
    const originalDetector = Object.getOwnPropertyDescriptor(globalThis, "BarcodeDetector");
    let calls = 0;

    class FakeBarcodeDetector {
        static async getSupportedFormats() {
            return ["ean_13", "upc_a"];
        }

        async detect() {
            calls += 1;
            return calls === 1
                ? [{ rawValue: "not-a-retail-code", format: "code_128" }]
                : [{ rawValue: "012345000058", format: "upc_a" }];
        }
    }

    Object.defineProperty(globalThis, "BarcodeDetector", {
        configurable: true,
        value: FakeBarcodeDetector,
    });

    try {
        const detections: Array<{ rawValue: string; format: string }> = [];
        const session = await startPhysicalBarcodeDecoder(
            { srcObject: null, async play() {} } as unknown as HTMLVideoElement,
            {} as MediaStream,
            (detection) => detections.push(detection),
        );
        await new Promise((resolve) => setTimeout(resolve, 180));
        session.stop();

        assert.deepEqual(detections, [{ rawValue: "012345000058", format: "upc_a" }]);
    } finally {
        if (originalDetector) {
            Object.defineProperty(globalThis, "BarcodeDetector", originalDetector);
        } else {
            Reflect.deleteProperty(globalThis, "BarcodeDetector");
        }
    }
});

test("external catalogue suggestions remain separate and require an explicit apply action", async () => {
    const component = await readFile(
        new URL("../components/physical/PhysicalGameCapture.tsx", import.meta.url),
        "utf8",
    );
    const suggestionSection = component.slice(
        component.indexOf("External catalogue suggestions"),
        component.indexOf("</section>", component.indexOf("External catalogue suggestions")),
    );

    assert.match(suggestionSection, /Search my library/);
    assert.match(suggestionSection, /Use for a new manual game/);
    assert.match(suggestionSection, /never selects or saves a game/);
    assert.doesNotMatch(suggestionSection, /selectExistingGame/);
    assert.match(component, /setSelectedGame\(null\)/);
    assert.match(component, /confirmed: true/);
    assert.doesNotMatch(component, /providerStatus|rawProviderError|credential/i);
});
