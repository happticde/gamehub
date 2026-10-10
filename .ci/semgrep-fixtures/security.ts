// ruleid: gamehub-javascript-dynamic-eval
eval(userInput);

// ruleid: gamehub-javascript-tls-verification-disabled
const insecure = { rejectUnauthorized: false };

// ok: gamehub-javascript-tls-verification-disabled
const secure = { rejectUnauthorized: true };
