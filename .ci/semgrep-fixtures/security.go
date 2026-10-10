package fixtures

import "crypto/tls"

// ruleid: gamehub-go-tls-verification-disabled
var insecure = tls.Config{InsecureSkipVerify: true}

// ok: gamehub-go-tls-verification-disabled
var secure = tls.Config{InsecureSkipVerify: false}
