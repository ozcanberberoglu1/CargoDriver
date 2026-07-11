using UnityEngine;

public class CargoAutoParent : MonoBehaviour
{
    private Rigidbody rb;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void OnCollisionStay(Collision collision)
    {
        // Only used in LobbyScene to load legos onto the truck. In GameScene the
        // cargo is free physics (no parenting) so auto-parenting must not run.
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "GameScene")
            return;

        if (rb == null || transform.parent != null) return;
        if (CargoPickup.heldByPickup.Contains(transform)) return;

        CarControl car = collision.gameObject.GetComponentInParent<CarControl>();
        if (car == null) return;

        if (rb.linearVelocity.magnitude < 0.5f)
        {
            Transform cargoParent = car.transform.Find("CargoBoxes");
            if (cargoParent != null)
                transform.SetParent(cargoParent, true);
            else
                transform.SetParent(car.transform, true);
        }
    }
}
