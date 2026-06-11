"use strict";

const form = document.getElementById("loginForm");
const errorBox = document.getElementById("loginError");

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  errorBox.classList.add("d-none");

  const username = document.getElementById("username").value.trim();
  const password = document.getElementById("password").value.trim();

  try {
    const resp = await fetch("/api/auth/login", {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password })
    });

    if (!resp.ok) {
      throw new Error("Login failed");
    }

    window.location.href = "/admin.html";
  } catch (err) {
    errorBox.textContent = "Invalid credentials or server error.";
    errorBox.classList.remove("d-none");
  }
});
