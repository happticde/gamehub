// SPDX-FileCopyrightText: 2026 jfk1987 and GameHub contributors
// SPDX-License-Identifier: AGPL-3.0-only
package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func fixtureFile(t *testing.T, root, name, content string) {
	t.Helper()
	target := filepath.Join(root, name)
	if err := os.MkdirAll(filepath.Dir(target), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(target, []byte(content), 0644); err != nil {
		t.Fatal(err)
	}
}
func expectFailure(t *testing.T, fragment string, action func()) {
	t.Helper()
	defer func() {
		value := recover()
		if value == nil || !strings.Contains(value.(string), fragment) {
			t.Fatalf("expected %q, got %v", fragment, value)
		}
	}()
	action()
}
func TestProductionNpmNoticesFollowInstalledLockfileAndPreserveVendoredNotices(t *testing.T) {
	root := t.TempDir()
	fixtureFile(t, root, "package-lock.json", `{"packages":{"":{},"node_modules/runtime":{"version":"1.0.0","license":"MIT"},"node_modules/tool":{"version":"1.0.0","license":"MIT","dev":true},"node_modules/other-platform":{"version":"1.0.0","license":"MIT","optional":true}}}`)
	fixtureFile(t, root, "node_modules/runtime/package.json", `{"name":"runtime","version":"1.0.0","license":"MIT","author":"Original author"}`)
	fixtureFile(t, root, "node_modules/runtime/LICENSE", "Copyright Original author; original license text")
	fixtureFile(t, root, "node_modules/runtime/dist/compiled/vendor/LICENSE", "Vendored author's notice")
	fixtureFile(t, root, "node_modules/runtime/node_modules/unrelated/LICENSE", "Not part of this package")
	fixtureFile(t, root, "standard/MIT.txt", "Complete MIT license")
	b := bundle{texts: map[string]string{}}
	b.npm(root, root, true)
	if len(b.records) != 1 || b.records[0].ID != "npm:runtime@1.0.0" {
		t.Fatalf("wrong runtime inventory: %+v", b.records)
	}
	if len(b.records[0].Notices) != 3 {
		t.Fatalf("lost original/vendor/standard text: %+v", b.records[0])
	}
	out := filepath.Join(root, "out")
	fixtureFile(t, root, "LICENSE", "AGPL fixture")
	b.write(out, root, "")
	result := string(read(filepath.Join(out, "THIRD_PARTY_NOTICES.txt")))
	if !strings.Contains(result, "Vendored author's notice") || strings.Contains(result, "Not part of this package") {
		t.Fatal("incorrect notice scope")
	}
	first := string(read(filepath.Join(out, "dependency-inventory.json")))
	b.write(out, root, "")
	if first != string(read(filepath.Join(out, "dependency-inventory.json"))) {
		t.Fatal("non-deterministic inventory")
	}
}
func TestMissingRequiredOrMismatchedNpmPackageFails(t *testing.T) {
	root := t.TempDir()
	fixtureFile(t, root, "package-lock.json", `{"packages":{"node_modules/runtime":{"version":"1.0.0","license":"MIT"}}}`)
	b := bundle{texts: map[string]string{}}
	expectFailure(t, "Missing installed package", func() { b.npm(root, root, true) })
	fixtureFile(t, root, "node_modules/runtime/package.json", `{"name":"runtime","version":"2.0.0"}`)
	expectFailure(t, "differs from lockfile", func() { b.npm(root, root, true) })
}
func TestMetadataFallbackRequiresCompleteLicenseAndKeepsAttribution(t *testing.T) {
	root := t.TempDir()
	fixtureFile(t, root, "package/package.json", `{"name":"original","author":"Original author","license":"MIT"}`)
	b := bundle{texts: map[string]string{}}
	expectFailure(t, "Missing license text", func() {
		b.add(record{ID: "original", License: "MIT"}, filepath.Join(root, "package"), filepath.Join(root, "package/package.json"), root)
	})
	fixtureFile(t, root, "standard/MIT.txt", "Complete MIT license")
	b.add(record{ID: "original", License: "MIT"}, filepath.Join(root, "package"), filepath.Join(root, "package/package.json"), root)
	if len(b.records) != 1 || !b.records[0].MetadataOnly || len(b.records[0].Notices) != 2 {
		t.Fatal("metadata-only fallback not explicit or incomplete")
	}
}
