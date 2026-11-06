package app.reach.android.privacy

import app.reach.android.mcp.ToolError

enum class AppRule(val key: String, val label: String) {
    ALLOW("allow", "Allow"), READ_ONLY("read_only", "Read only"), BLOCK("block", "Block");

    companion object {
        fun of(s: String?): AppRule = entries.firstOrNull { it.key == s } ?: ALLOW
    }
}

/**
 * App rules (spec/POLICY.md): banking, payment, wallet, password-manager and authenticator apps are blocked out of
 * the box; the user's own rules (set in the app, by package) win over that. Read only: the app can be read, and
 * scrolled or left with back/home, but nothing in it is tapped or typed.
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

    /** The user's rules, package → rule. Loaded by Settings; empty in unit tests. */
    @Volatile var userRules: Map<String, AppRule> = emptyMap()

    /** Blocked out of the box (before the user's rules). [label]: the app's name as the user sees it, when known. */
    fun builtinBlocked(pkg: String?, label: String? = null): Boolean {
        if (pkg.isNullOrEmpty()) return false
        if (pkg in blockedPackages) return true
        val p = pkg.lowercase()
        // Package names: whole dotted parts only ("bank" in "com.hdfcbank.app" counts, "banking" too).
        if (blockedWords.any { w -> p.split('.').any { part -> part.contains(w) } }) return true
        val l = label?.lowercase() ?: return false
        return blockedWords.any { l.contains(it) }
    }

    fun rule(pkg: String?, label: String? = null, user: Map<String, AppRule> = userRules): AppRule {
        if (pkg.isNullOrEmpty()) return AppRule.ALLOW
        user[pkg]?.let { return it }
        return if (builtinBlocked(pkg, label)) AppRule.BLOCK else AppRule.ALLOW
    }

    fun isBlocked(pkg: String?, label: String? = null): Boolean = rule(pkg, label) == AppRule.BLOCK

    /** For actions: refuses in blocked and read-only apps. */
    fun requireCanAct(pkg: String?, name: String? = pkg) {
        when (rule(pkg)) {
            AppRule.BLOCK -> throw ToolError("blocked_app", "$name is blocked by Reach privacy rules")
            AppRule.READ_ONLY -> throw ToolError("read_only_app",
                "$name is read-only in Reach: it can be read but not used. The user can change this in the Reach app (App rules)")
            AppRule.ALLOW -> {}
        }
    }
}
