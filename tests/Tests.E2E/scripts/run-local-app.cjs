const { spawn, spawnSync } = require("node:child_process");

function sleep(milliseconds) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

function parseArgs(argv) {
  const args = {};

  for (let index = 2; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];

    if (key === "--base-url" && value) {
      args.baseUrl = value;
    }
  }

  return args;
}

function formatDockerFailure(message) {
  return [
    "Docker is required for local Playwright E2E because the suite starts a temporary PostgreSQL container.",
    "Start Docker Desktop or another local Docker engine, then rerun the Playwright command.",
    `Original Docker error: ${message}`
  ].join("\n");
}

function runCommand(command, args) {
  const result = spawnSync(command, args, {
    stdio: ["ignore", "pipe", "pipe"],
    encoding: "utf8"
  });

  if (result.status !== 0) {
    const message = (result.stderr || result.stdout || `Command failed: ${command}`).trim();
    if (command === "docker") {
      throw new Error(formatDockerFailure(message));
    }

    throw new Error(message);
  }

  return result.stdout.trim();
}

function parseMappedPort(output) {
  const parts = output.trim().split(":");
  const port = parts[parts.length - 1];

  if (!port || Number.isNaN(Number(port))) {
    throw new Error(`Could not parse mapped PostgreSQL port from: ${output}`);
  }

  return port;
}

function waitForContainer(containerId) {
  for (let attempt = 0; attempt < 30; attempt += 1) {
    const result = spawnSync("docker", ["exec", containerId, "pg_isready", "-U", "postgres"], {
      stdio: "ignore"
    });

    if (result.status === 0) {
      return;
    }

    sleep(1000);
  }

  throw new Error("Timed out waiting for PostgreSQL container readiness.");
}

const { baseUrl = "http://127.0.0.1:5005" } = parseArgs(process.argv);
const image = process.env.PLAYWRIGHT_POSTGRES_IMAGE || "postgres:16-alpine";
const database = process.env.PLAYWRIGHT_DB_NAME || "servisotest";
const username = process.env.PLAYWRIGHT_DB_USER || "postgres";
const password = process.env.PLAYWRIGHT_DB_PASSWORD || "postgres";
const loginEmail = (process.env.E2E_EMAIL || process.env.E2E_USERNAME || "manager@test.com").trim();

let containerId = "";
let appProcess = null;
let shuttingDown = false;

function cleanup(exitCode) {
  if (shuttingDown) {
    return;
  }

  shuttingDown = true;

  if (appProcess && !appProcess.killed) {
    appProcess.kill("SIGTERM");
  }

  if (containerId) {
    spawnSync("docker", ["stop", containerId], { stdio: "ignore" });
  }

  process.exit(exitCode);
}

process.on("SIGINT", () => cleanup(130));
process.on("SIGTERM", () => cleanup(143));

try {
  runCommand("docker", ["info"]);

  containerId = runCommand("docker", [
    "run",
    "--rm",
    "-d",
    "-P",
    "-e",
    `POSTGRES_DB=${database}`,
    "-e",
    `POSTGRES_USER=${username}`,
    "-e",
    `POSTGRES_PASSWORD=${password}`,
    image
  ]);

  waitForContainer(containerId);

  const mappedPort = parseMappedPort(runCommand("docker", ["port", containerId, "5432/tcp"]));
  const connectionString = `Host=127.0.0.1;Port=${mappedPort};Database=${database};Username=${username};Password=${password};SSL Mode=Disable;Trust Server Certificate=true`;

  console.log(`[pw-e2e] Using disposable PostgreSQL container ${containerId} on port ${mappedPort}`);
  console.log(`[pw-e2e] Login email: ${loginEmail}`);

  appProcess = spawn(
    "dotnet",
    [
      "run",
      "--project",
      "./ServiceTaskPlanningSystem.csproj",
      "--no-launch-profile",
      "--",
      "--urls",
      baseUrl
    ],
    {
      cwd: process.cwd(),
      stdio: "inherit",
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT || "Development",
        ASPNETCORE_URLS: baseUrl,
        ConnectionStrings__DefaultConnection: connectionString,
        E2E_TEST_MODE: "true",
        HEIGIT_API_KEY: process.env.HEIGIT_API_KEY || "E2E_FAKE_KEY",
        HEIGIT_BASE_URL: process.env.HEIGIT_BASE_URL || "https://e2e.invalid",
        E2E_EMAIL: loginEmail
      }
    }
  );

  appProcess.on("exit", (code) => cleanup(code ?? 1));
} catch (error) {
  console.error("[pw-e2e] Failed to start local E2E app environment.");
  console.error(error instanceof Error ? error.message : error);
  cleanup(1);
}