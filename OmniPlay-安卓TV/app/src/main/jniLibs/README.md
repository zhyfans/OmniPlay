The default build gets Android libmpv from the Gradle dependency
`dev.jdtech.mpv:libmpv:0.5.1`.

Only place files here when overriding the bundled libmpv build manually.
Expected override layout:

arm64-v8a/libmpv.so
armeabi-v7a/libmpv.so
x86_64/libmpv.so

The Java/C++ bridge loads libmpv dynamically, so it does not link against mpv
headers.
