// Sign-in page. On success the hub sets an HttpOnly cookie, which this script can't read
// (on purpose); it just moves on to the app.

const form = document.getElementById('login');
const error = document.getElementById('error');

// Already signed in? Go straight to the app.
fetch('/api/whoami', { credentials: 'same-origin' }).then(r => { if (r.ok) location.replace('/'); });

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  error.textContent = '';
  const button = form.querySelector('button');
  button.disabled = true;
  try {
    const res = await fetch('/auth/login', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-Switchboard': '1' },
      body: JSON.stringify({
        password: document.getElementById('password').value,
        remember: document.getElementById('remember').checked,
      }),
    });
    if (res.ok) {
      location.replace('/');
      return;
    }
    if (res.status === 429) {
      error.textContent = 'Too many tries. Wait a minute and try again.';
    } else {
      const data = await res.json().catch(() => null);
      error.textContent = data?.error ?? `Sign-in failed (HTTP ${res.status}).`;
    }
  } catch {
    error.textContent = "Can't reach the hub. Is it running?";
  } finally {
    button.disabled = false;
  }
});
