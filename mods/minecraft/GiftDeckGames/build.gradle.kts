// GiftDeck Games: a Paper plugin with streamer-vs-viewers mini-games driven by server commands (MIT).
// Not part of GiftDeck's own build (GiftDeck.csproj excludes mods\**).
//
// Build:  gradlew build        -> build/libs/GiftDeckGames.jar
//         gradlew copyToPack   -> also copies it to Packs/minecraft/plugins/ (the jar GiftDeck ships)
//
// Compiled against the Paper 1.20.1 API for Java 17, so the one jar runs on Paper 1.20.1 up to 26.x.

plugins {
    java
}

group = "io.github.obiwayne"
version = "1.0.0"

repositories {
    mavenCentral()
    maven("https://repo.papermc.io/repository/maven-public/")
}

dependencies {
    compileOnly("io.papermc.paper:paper-api:1.20.1-R0.1-SNAPSHOT")
}

tasks.withType<JavaCompile>().configureEach {
    options.release.set(17)
    options.encoding = "UTF-8"
}

tasks.processResources {
    val props = mapOf("version" to project.version)
    inputs.properties(props)
    filesMatching("plugin.yml") { expand(props) }
}

tasks.jar {
    archiveFileName.set("GiftDeckGames.jar")
    from(rootProject.file("LICENSE"))
}

tasks.register<Copy>("copyToPack") {
    dependsOn(tasks.jar)
    from(tasks.jar.flatMap { it.archiveFile })
    into(rootProject.file("../../../Packs/minecraft/plugins"))
}
