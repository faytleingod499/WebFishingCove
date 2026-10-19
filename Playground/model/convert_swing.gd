# Turns the imported swing set (res://swing.glb) into a self-contained scene (res://swing.scn) with plain
# materials. The seats are the nodes "Seat0" and "Seat1", each with its origin on the top bar.
extends SceneTree

func _init():
	var root = load("res://swing.glb").instance()
	_redo(root)
	_own(root, root)
	var out = PackedScene.new()
	print("pack ", out.pack(root))
	print("save ", ResourceSaver.save("res://swing.scn", out, ResourceSaver.FLAG_COMPRESS))
	for n in ["Seat0", "Seat1", "Frame", "TopBar"]:
		var f = root.find_node(n, true, false)
		print(n, " ", f != null, " ", f.transform.origin if f else "")
	quit()

func _redo(n):
	if n is MeshInstance:
		for i in range(n.mesh.get_surface_count()):
			var old = n.mesh.surface_get_material(i)
			var m = SpatialMaterial.new()
			m.resource_name = old.resource_name
			m.albedo_color = old.albedo_color
			m.roughness = old.roughness
			m.metallic = old.metallic
			m.metallic_specular = 0.3
			n.mesh.surface_set_material(i, m)
	for c in n.get_children():
		_redo(c)

func _own(n, r):
	for c in n.get_children():
		c.owner = r
		_own(c, r)
