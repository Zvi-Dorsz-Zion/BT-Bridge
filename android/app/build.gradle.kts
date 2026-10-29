import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.btbridge.app"
    compileSdk = 36
    buildToolsVersion = "36.1.0"

    defaultConfig {
        applicationId = "com.btbridge.app"
        minSdk = 29
        targetSdk = 34
        // בכל גרסה חדשה להגדיל את versionCode, אחרת הטלפון לא יתקין עדכון
        versionCode = 6
        versionName = "0.2.4"
    }

    // חתימה קבועה: signing/btbridge.jks + signing/keystore.properties (לגבות! בלי הקובץ אי אפשר להתקין עדכונים)
    val ks = Properties().apply {
        val f = rootProject.file("signing/keystore.properties")
        if (f.exists()) f.inputStream().use { load(it) }
    }
    signingConfigs {
        create("fixed") {
            storeFile = rootProject.file("signing/" + ks.getProperty("storeFile", "btbridge.jks"))
            storePassword = ks.getProperty("storePassword")
            keyAlias = ks.getProperty("keyAlias")
            keyPassword = ks.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            // מכווץ את ה-APK (בעיקר ספריית האייקונים) מ-38MB לכמה MB
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
            signingConfig = signingConfigs.getByName("fixed")
        }
        debug {
            signingConfig = signingConfigs.getByName("fixed")
        }
    }

    lint {
        checkReleaseBuilds = false
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2024.12.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")

    // בדיקת תאימות מול צד Windows: org.json האמיתי, כי זה של אנדרואיד הוא רק stub ב-JVM
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
