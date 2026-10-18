# Turns an imported pet .glb (res://pet.glb) into a self-contained pet scene (res://pet.scn):
# its own plain materials (recoloured by the mod at run time) and looping animations.
extends SceneTree

func _init():
	var root = load("res://pet.glb").instance()
	var mi = _find_mesh(root)
	var mesh = mi.mesh
	for i in range(mesh.get_surface_count()):
		var old = mesh.surface_get_material(i)
		var m = SpatialMaterial.new()
		m.resource_name = old.resource_name
		m.albedo_color = old.albedo_color
		m.roughness = 1.0
		m.metallic = 0.0
		m.metallic_specular = 0.2
		mesh.surface_set_material(i, m)
	var ap = root.find_node("AnimationPlayer", true, false)
	for a in ap.get_animation_list():
		ap.get_animation(a).loop = true
	_own(root, root)
	var out = PackedScene.new()
	print("pack ", out.pack(root))
	print("save ", ResourceSaver.save("res://pet.scn", out, ResourceSaver.FLAG_COMPRESS))
	quit()

func _find_mesh(n):
	if n is MeshInstance:
		return n
	for c in n.get_children():
		var r = _find_mesh(c)
		if r:
			return r
	return null

func _own(n, r):
	for c in n.get_children():
		c.owner = r
		_own(c, r)
