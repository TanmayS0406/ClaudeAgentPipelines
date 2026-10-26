// Checkout page: opens the Cashfree payment window on this page (a pop-up, not a redirect) and follows the payment
// until it is settled. The customer may pay on their phone, where Cashfree shows its own "payment successful" page,
// so this page does not wait to be redirected to: it asks the app for the payment status every few seconds, and
// the app asks Cashfree. When the payment is settled (paid, failed or expired) it moves on to the Billing page.
(function () {
    const holder = document.getElementById('checkout');
    if (!holder) return;

    const progress = document.getElementById('checkout-progress');
    const statusText = document.getElementById('checkout-status');
    const button = document.getElementById('open-checkout');
    const pollEveryMs = 3000;
    let finished = false;

    function say(text, working) {
        statusText.textContent = text;
        const spinner = progress.querySelector('.spinner');
        if (spinner) spinner.hidden = !working;
    }

    // The app's own return page confirms the payment with Cashfree and shows the result on the Billing page.
    function finish() {
        if (finished) return;
        finished = true;
        say('Payment finished. Checking the result...', true);
        location.href = holder.dataset.doneUrl;
    }

    async function poll() {
        while (!finished) {
            try {
                const response = await fetch(holder.dataset.statusUrl, { cache: 'no-store' });
                if (response.ok) {
                    const payment = await response.json();
                    if (payment.status !== 'Pending') {
                        finish();
                        return;
                    }
                    if (payment.problem) say(payment.problem + ' Trying again...', true);
                }
            } catch {
                // the app or the network blinked: try again
            }
            await new Promise(resolve => setTimeout(resolve, pollEveryMs));
        }
    }

    function open() {
        if (typeof Cashfree !== 'function') {
            say('Could not load the Cashfree checkout. Check your internet connection and reload this page.', false);
            return;
        }
        say('Waiting for the payment...', true);
        const cashfree = Cashfree({ mode: holder.dataset.mode });
        cashfree.checkout({ paymentSessionId: holder.dataset.session, redirectTarget: '_modal' }).then(result => {
            // The window reports a finished attempt whatever its outcome (a declined card too, which can be retried),
            // so only Cashfree's word on the order, picked up by the polling, ends this page.
            if (result && result.paymentDetails) say('Confirming the payment with Cashfree...', true);
            // Closing the window does not cancel a payment that is under way on a phone; polling carries on.
            else if (result && result.error) say('The payment window was closed. Waiting in case you paid on your phone...', true);
        });
    }

    button.addEventListener('click', open);
    poll();
    open();
})();
