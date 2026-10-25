// Sign in with Claude: opens `claude auth login` on this computer, then polls until Claude Code reports
// a signed-in account and continues into the app.
(function () {
    const button = document.getElementById('start-login');
    if (!button) return;

    const progress = document.getElementById('login-progress');
    const progressText = document.getElementById('login-progress-text');
    const errorBox = document.getElementById('login-error');
    const continueForm = document.getElementById('continue-form');
    const token = continueForm.querySelector('input[name="__RequestVerificationToken"]').value;
    let initialAccount = null;
    let polling = false;

    function showError(text) {
        errorBox.textContent = text;
        errorBox.hidden = false;
        progress.hidden = true;
        button.disabled = false;
    }

    async function poll() {
        if (polling) return;
        polling = true;
        while (true) {
            await new Promise(resolve => setTimeout(resolve, 3000));
            let status;
            try {
                const response = await fetch(button.dataset.statusUrl, { cache: 'no-store' });
                status = await response.json();
            } catch {
                continue; // transient: try again
            }

            // A different account counts too ("Use a different Claude account").
            if (status.loggedIn && status.account !== initialAccount) {
                progressText.textContent = 'Signed in as ' + status.account + '. Continuing...';
                continueForm.submit();
                return;
            }
            if (!status.loginInProgress) {
                if (status.loggedIn) {
                    continueForm.submit();
                    return;
                }
                polling = false;
                showError('The Claude sign-in window was closed before sign-in finished. Click "Sign in with Claude" to try again.');
                return;
            }
        }
    }

    button.addEventListener('click', async () => {
        button.disabled = true;
        errorBox.hidden = true;
        try {
            const current = await (await fetch(button.dataset.statusUrl, { cache: 'no-store' })).json();
            initialAccount = current.loggedIn ? current.account : null;

            const response = await fetch(button.dataset.startUrl, {
                method: 'POST',
                headers: { 'RequestVerificationToken': token }
            });
            if (!response.ok) {
                const body = await response.json().catch(() => ({}));
                showError(body.error || 'Could not start the Claude sign-in.');
                return;
            }
            progress.hidden = false;
            poll();
        } catch (e) {
            showError('Could not start the Claude sign-in: ' + e.message);
        }
    });

    // The page was reloaded while a sign-in window is still open.
    if (!progress.hidden) {
        button.disabled = true;
        poll();
    }
})();
