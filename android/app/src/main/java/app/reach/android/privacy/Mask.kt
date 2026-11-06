package app.reach.android.privacy

/**
 * Hides secrets in text before it leaves the phone (spec/POLICY.md "Masking"): one-time codes next to words like
 * "OTP" or "code", and card numbers. Plain Kotlin, tested on the PC (PrivacyTest). The agent can ask for the real
 * text with reveal=true, which needs the user's approval on the phone.
 */
object Mask {
    private val otpWords = Regex(
        "(?i)\\b(otp|one[ -]?time|passcode|pass code|verification|verify|security code|login code|auth(entication)? code|2fa|pin|code)\\b|ओटीपी|कोड",
    )
    /** 4–8 digits standing alone; "12.50" and "1,234" are amounts, but a full stop ending the sentence is fine. */
    private val shortNumber = Regex("(?<!\\d)(?<!\\d[.,])\\d{4,8}(?!\\d)(?![.,]\\d)")
    private val cardLike = Regex("(?<!\\d)(?:\\d[ -]?){12,18}\\d(?!\\d)")

    data class Result(val text: String, val masked: Boolean)

    fun apply(text: String): Result {
        var out = text
        var masked = false
        out = cardLike.replace(out) { m ->
            val digits = m.value.filter(Char::isDigit)
            if (digits.length in 13..19 && luhn(digits)) {
                masked = true
                "•••• " + digits.takeLast(4)
            } else m.value
        }
        if (otpWords.containsMatchIn(out)) {
            out = shortNumber.replace(out) { m ->
                // Years and amounts are not codes.
                val v = m.value
                if (v.length == 4 && v.startsWith("19") || v.length == 4 && v.startsWith("20")) v
                else { masked = true; "•".repeat(v.length) }
            }
        }
        return Result(out, masked)
    }

    fun luhn(digits: String): Boolean {
        var sum = 0
        var double = false
        for (i in digits.indices.reversed()) {
            var d = digits[i] - '0'
            if (double) { d *= 2; if (d > 9) d -= 9 }
            sum += d
            double = !double
        }
        return sum % 10 == 0
    }

    const val NOTE = "(codes and card numbers hidden; reveal=true asks the user on the phone)"
}
