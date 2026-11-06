package app.reach.android.privacy

/**
 * The privacy rules that apply from the first phase (spec/POLICY.md): banking, payment, wallet, password-manager and
 * authenticator apps are never read or acted on. The full per-app policy file and approvals come in P5.
 */
object Privacy {
    private val blockedPackages = setOf(
        "com.google.android.apps.nbu.paisa.user", // Google Pay (India)
        "com.google.android.apps.walletnfcrel",   // Google Wallet
        "com.phonepe.app",
        "net.one97.paytm",
        "in.org.npci.upiapp",                     // BHIM
        "com.paypal.android.p2pmobile",
        "com.x8bit.bitwarden",
        "com.onepassword.android",
        "com.agilebits.onepassword",
        "keepass2android.keepass2android",
        "com.kunzisoft.keepass.libre",
        "com.lastpass.lpandroid",
        "com.dashlane",
        "com.google.android.apps.authenticator2",
        "com.azure.authenticator",
        "com.authy.authy",
    )

    private val blockedWords = listOf("bank", "upi", "wallet", "password", "authenticator", "vault", "2fa", "otp")

    /** [label]: the app's name as the user sees it, when known. */
    fun isBlocked(pkg: String?, label: String? = null): Boolean {
        if (pkg.isNullOrEmpty()) return false
        if (pkg in blockedPackages) return true
        val p = pkg.lowercase()
        // Package names: whole dotted parts only ("bank" in "com.hdfcbank.app" counts, "banking" too).
        if (blockedWords.any { w -> p.split('.').any { part -> part.contains(w) } }) return true
        val l = label?.lowercase() ?: return false
        return blockedWords.any { l.contains(it) }
    }
}
