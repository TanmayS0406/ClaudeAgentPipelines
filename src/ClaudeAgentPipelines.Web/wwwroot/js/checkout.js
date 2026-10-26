// Checkout page: opens the Cashfree payment page for the payment session. Cashfree sends the customer back to the
// app when they are done, and the app then asks Cashfree whether the order was paid.
(function () {
    const holder = document.getElementById('checkout');
    if (!holder) return;

    const progress = document.getElementById('checkout-progress');
    const button = document.getElementById('open-checkout');

    function open() {
        if (typeof Cashfree !== 'function') {
            progress.textContent = 'Could not load the Cashfree checkout. Check your internet connection and reload this page.';
            return;
        }
        const cashfree = Cashfree({ mode: holder.dataset.mode });
        cashfree.checkout({ paymentSessionId: holder.dataset.session, redirectTarget: '_self' });
    }

    button.addEventListener('click', open);
    open();
})();
