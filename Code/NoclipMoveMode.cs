using Sandbox.Movement;

public sealed class NoclipMoveMode : Sandbox.Movement.MoveMode
{
	private bool m_IsEnabled = false;
	private float m_SpeedBoostMultiplier = 1.0f;

	protected override void OnUpdateAnimatorState( SkinnedModelRenderer renderer )
	{
		renderer.Set( "b_noclip", true );
		renderer.Set( "duck", 0f );
	}

	public override int Score( PlayerController controller )
	{
		if (Input.Keyboard.Pressed("N"))
			m_IsEnabled ^= true;

		if (m_IsEnabled)
			return 1000;

		return -1;
	}

	public override void UpdateRigidBody( Rigidbody body )
	{
		body.Gravity = false;
		body.LinearDamping = 5.0f;
		body.AngularDamping = 1f;
	}

	public override void OnModeBegin()
	{
		Controller.IsClimbing = true;
		
		Controller.Body.Gravity = false;

		Controller.BodyCollider.IsTrigger = true;
		Controller.FeetCollider.IsTrigger = true;
	}

	public override void OnModeEnd( MoveMode next )
	{
		Controller.IsClimbing = false;
		Controller.Body.Velocity = Controller.Body.Velocity.ClampLength( Controller.RunSpeed );

		Controller.BodyCollider.IsTrigger = false;
		Controller.FeetCollider.IsTrigger = false;

		Controller.Renderer.Set( "b_noclip", false );
	}

	public override Transform CalculateEyeTransform()
	{
		var transform = base.CalculateEyeTransform();

		// Undo the camera lowering that IsDucking causes
		if ( Controller.IsDucking )
			transform.Position += Vector3.Up * (Controller.BodyHeight - Controller.DuckedHeight);

		return transform;
	}

	public override Vector3 UpdateMove( Rotation eyes, Vector3 input )
	{
		// don't normalize, because analog input might want to go slow
		input = input.ClampLength( 1 );

		var direction = eyes * input;

		// Run if we're holding down alt move button
		bool run = Input.Down( Controller.AltMoveButton );

		// if Run is default, flip that logic
		if ( Controller.RunByDefault ) run = !run;

		m_SpeedBoostMultiplier += Input.MouseWheel.y;
		m_SpeedBoostMultiplier = m_SpeedBoostMultiplier.Clamp(1.0f, 50.0f);

		if ( direction.IsNearlyZero( 0.1f ) )
		{
			direction = 0;
		}

		// if we're hold down jump move upwards
		if ( Input.Down( "jump" ) ) direction += Vector3.Up;

		// if we're hold down duck move downwards
		if ( Input.Down( "duck" ) ) direction += Vector3.Down;

		return direction * 1000.0f * m_SpeedBoostMultiplier;
	}

}
