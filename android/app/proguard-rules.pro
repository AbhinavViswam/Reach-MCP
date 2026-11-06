# kotlinx.serialization is used only through JsonElement (no @Serializable classes), so nothing extra to keep.

# ML Kit ships its own obfuscated classes; R8 renaming them crashed JARVIS's release build on first use
# (2026-10-02). Left exactly as Google ships them.
-keep class com.google.mlkit.** { *; }
-keep class com.google.android.gms.internal.mlkit_vision_** { *; }
